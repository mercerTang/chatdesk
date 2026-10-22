# 客服对话系统 — 设计

## 架构

- 后端：ASP.NET Core 8 Web API + SignalR，端口 5180，只提供 API 与 Hub，不托管页面。
- 数据：EF Core + SQLite（WAL 模式），库文件 `ChatServer/chat.db`，启动时 `EnsureCreated`；连接串带 `Default Timeout=5`，用于并发写等锁。
- 前端：`web/` 下的 Vue 3 + Vite + Element Plus 单页应用，依赖 vue-router、axios、`@microsoft/signalr`。
  - 开发：Vite dev server（5173）把 `/api`、`/hubs` 代理到 `http://localhost:5180`。
  - 生产：`npm run build` 产出 `web/dist`，由独立静态服务器托管，跨域经后端 CORS。
- 日志：Serilog（控制台 + 文件）、请求日志、全局异常处理（统一 `{ error }`）。

单实例部署。多实例需要 Redis 背板与跨实例投递，属于本设计之外的扩展。

## 数据模型

### Account
| 字段 | 说明 |
|---|---|
| Id | 主键 |
| Username | 唯一 |
| PasswordHash | PBKDF2-SHA256（100000 次、16 字节盐、32 字节密钥） |
| DisplayName | 展示名，最长 64，过滤控制字符 |
| Role | `visitor` / `agent` |
| IsOnline | 客服在线标记（由 SignalR 连接维护） |
| MaxConcurrentConversations | 客服并发上限，默认 5 |
| CreatedAt | 创建时间 |

### Conversation
| 字段 | 说明 |
|---|---|
| Id | 主键 |
| VisitorId / AgentId | 访客与客服账号 |
| Status | `active` / `closed` |
| LastMessageSeq | 会话内最后消息序号 |
| CreatedAt / LastActivityAt / ClosedAt | 创建 / 最后活跃 / 关闭时间 |

### Message
| 字段 | 说明 |
|---|---|
| Id | 主键 |
| ConversationId | 会话 |
| Seq | 会话内单调序号 |
| SenderId / SenderRole | 发送者 |
| Content | 文本，最长 2000 |
| ClientMessageId | 客户端消息 ID，可空，最长 100 |
| SentAt / DeliveredAt / ReadAt | 发送、送达、已读时间 |

索引：`(ConversationId, Seq)` 唯一；`(ConversationId, ClientMessageId)` 唯一（`ClientMessageId` 非空时）；`(VisitorId, AgentId)` 在 `Status='active'` 时唯一；`(AgentId, Status)`。

### Ticket
| 字段 | 说明 |
|---|---|
| Id | 主键 |
| TicketNo | 工单号，唯一，形如 `T-20261021-00010` |
| ConversationId | 关联会话，唯一（一会话一工单） |
| Status | `open` / `closed`，随会话关闭同步 |
| CreatedAt / ClosedAt | 创建 / 关闭时间 |

索引：`TicketNo` 唯一、`ConversationId` 唯一、`CreatedAt`。已有库在启动时补建该表，并为历史会话补建工单。

## 登录与鉴权

- JWT（HMAC-SHA256，8 小时有效期），Claims：账号 ID、角色、展示名。
- 访客注册接口只创建 `visitor`；客服账号由种子数据提供（agent1/agent2/agent3，密码 Agent@123456），不开放客服自助注册。
- 接口用 `[Authorize]` + 角色限制；SignalR 从 `access_token` 查询参数取 token。
- 种子幂等：仅当不存在客服账号时插入；启动时复位在线标记。

## 可靠性机制

1. **先落库后广播**：发送消息在一个数据库事务内完成序号分配与写入，提交成功后才向会话组推送；扇出使用与服务端生命周期绑定的令牌，不随发送方连接断开而取消。
2. **服务端权威序号**：`Seq = LastMessageSeq + 1` 与会话更新在同一事务；同会话进程内串行 + `(ConversationId, Seq)` 唯一索引兜底，冲突时重试；会话闸门引用归零后回收。
3. **幂等重发**：同一会话内相同 `ClientMessageId` 已存在时直接返回原消息，不新增记录、不重复广播。
4. **断线补拉**：`GET /api/conversations/{id}/messages?afterSeq=N` 返回序号更大的消息；客户端重连后按本地最后序号循环拉取（每页 200），直到排空。
5. **回执**：消息写入后回执含 `Seq`；接收方调用 `AckDelivered` / `AckRead` 更新状态并通知对端。
   - 回执事件携带确认方 `byAccountId`，客户端据此忽略自己的确认，避免把自己的消息标成已读/已送达。
   - 已读只在会话处于前台（被打开）时确认；后台会话收到消息只确认送达，未读状态保留。
6. **顺序与缺口**：客户端按 `Seq` 排序渲染；收到 `Seq` 大于本地最后序号 + 1 时先补拉再落消息。
7. **客户端发件箱**：断线时消息按会话进入发件箱，重连后按原 `ClientMessageId` 补发，服务端幂等去重。
8. **SQLite**：WAL + 连接串默认超时，降低读写互斥与锁失败。

## 数据保留

- 聊天记录以每条消息的 `SentAt` 为准，超过 `Retention:Days`（默认 30 天）后逐条删除；工单、会话与序号水位保留，删除后的消息不再返回。
- 清理在启动时执行一次，之后每 6 小时一次；删除条数写入日志。
- 工单页通过 `GET /api/tickets/meta` 获取并展示保留天数。
- 浏览器 IndexedDB 缓存按 30 天清理，与服务端策略一致。

## 接口

### REST
| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/auth/register` | 访客注册，返回 token |
| POST | `/api/auth/login` | 登录，返回 token 与角色 |
| GET | `/api/auth/me` | 当前身份 |
| GET | `/api/agents` | 客服列表：在线状态、活跃会话数、上限 |
| POST | `/api/conversations` | 创建或复用会话；可指定客服，缺省自动分配在线且有余量的最空闲客服；无可用客服 409 |
| GET | `/api/conversations/mine` | 访客看自己的会话；客服看分配给自己的会话 |
| GET | `/api/conversations/{id}` | 会话详情，仅成员 |
| GET | `/api/conversations/{id}/messages?afterSeq=N&limit=200` | 增量消息，仅成员 |
| POST | `/api/conversations/{id}/close` | 关闭会话，仅成员 |
| GET | `/api/tickets/meta` | 工单数据保留天数 |
| GET | `/api/tickets` | 客服的工单列表（按创建时间倒序） |
| GET | `/api/tickets/{id}` | 工单详情与聊天记录（仅服务该会话的客服） |
| GET | `/api/tickets/{id}/export?format=txt\|csv` | 导出工单记录，附件下载 |
| GET | `/health` | 健康检查 |

### SignalR Hub `/hubs/chat`
| 方法/事件 | 说明 |
|---|---|
| `JoinConversation(conversationId)` | 校验成员身份并加入会话组；客服连接时加入自己的客服组与全部进行中会话 |
| `SendMessage(conversationId, content, clientMessageId)` | 落库后广播，返回含 `Seq` 的消息回执 |
| `AckDelivered(conversationId, upToSeq)` | 标记序号不大于 `upToSeq` 的对方消息为已送达 |
| `AckRead(conversationId, upToSeq)` | 标记已读 |
| `ReceiveMessage(message)` | 服务端推送新消息 |
| `MessageDelivered({ conversationId, upToSeq, byAccountId, confirmedAt })` | 对端已送达 |
| `MessageRead({ conversationId, upToSeq, byAccountId, confirmedAt })` | 对端已读 |
| `ConversationCreated(conversation)` | 新会话，推给对应客服 |
| `ConversationClosed({ conversationId, closedAt })` | 会话被关闭 |
| `AgentPresenceChanged({ agentId, isOnline, activeConversationCount })` | 客服上下线 |

## 前端

视觉：默认月之暗面深色主题，右上角「亮色」开关切换暗/亮（存 `localStorage`）；使用 Kimi 品牌色板（`#121212` / `#007CFF` / `#00A1FF` / `#A0DAF7` / `#002F5B`）；状态色统一为品牌蓝与灰（在线/成功为蓝，离线/中性为灰，提醒为月光蓝），不引入绿/橙/红。登录页以月盘为主视觉，配月盘漂浮、光晕漂移、文字入场、页面切换与消息入场动效，并尊重 `prefers-reduced-motion`；输入框是悬浮在消息区上方的磨砂玻璃面板，消息从玻璃后方滚过。

- `web/`：Vue 3 + Vite + Element Plus。
- `src/views/Login.vue`：访客登录 / 注册、客服登录；Element Plus 表单校验。
- `src/views/VisitorChat.vue`：不展示客服/好友列表，登录后自动匹配一位在线客服；单会话消息线程、三段回执、断线重连与循环补拉。
- `src/views/AgentConsole.vue`：按访客一行的会话列表（同一访客只出现一行，优先进行中）、切换回复、新会话实时提醒、并发上限取接口值；顶栏提供「工单记录」入口。
- `src/views/Tickets.vue`：工单记录页（客服专属），左侧按创建时间倒序列出工单，右侧按时间正序展示聊天记录，支持导出 TXT / CSV。
- `src/lib/chatStore.js`：按会话缓存消息、Seq 去重、`ClientMessageId` 合并乐观消息、缺口补拉、发件箱、回执过滤（`byAccountId`）与前台会话已读确认。
- `src/lib/localDb.js`：聊天记录写入 IndexedDB；打开会话先读本地缓存渲染，再按缺口向服务端对账（缓存缺段时从 0 补拉）。
- 路由守卫按角色（visitor → `/chat`，agent → `/agent`，未登录 → `/login`）；axios 拦截器带 token，401 清登录态跳登录页；token 存 `localStorage`。

## 安全边界

- JWT 密钥与 CORS 为开发配置，生产部署需替换密钥并按域名收紧 CORS。
- 本轮不做密码找回、刷新 token、邮箱验证、多设备会话管理。

## 验证方法

- 后端：`dotnet build` 无错误；`dotnet test` 覆盖并发序号、幂等重发、客服并发上限、增量补拉、鉴权 401/403、回执身份、展示名校验、工单与导出、30 天消息清理、客服离线/断线恢复。
- 前端：`cd web && npm run build` 成功。
- 浏览器：Vue 版完成访客注册登录 → 选择客服 → 双端收发 → 回执 → 断线重连补拉；旧静态页面已删除。
