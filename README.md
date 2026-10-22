# 在线客服对话系统

访客登录后找到客服、客服 1 对多接待的实时对话系统。前后端分离：

- 后端 `ChatServer/`：ASP.NET Core 8 Web API + SignalR + EF Core（SQLite），端口 5180
- 前端 `web/`：Vue 3 + Vite + Element Plus + vue-router + axios + SignalR 客户端，开发端口 5173

## 目录结构

```
chatdesk-copy/
├── ChatServer.sln
├── ChatServer/                 # 后端（仅 API 与 Hub，不再托管页面）
│   ├── Program.cs              # 启动：Serilog / EF / JWT / SignalR / 全局异常 / 建库种子
│   ├── Models/                 # Account、Conversation、Message、角色与会话状态常量
│   ├── Data/                   # AppDbContext（唯一索引）、DateTimeOffset 转换器
│   ├── Dtos/                   # 请求与响应契约
│   ├── Services/               # PBKDF2、JWT、会话与消息领域服务、在线状态、建库初始化
│   ├── Controllers/            # Auth / Agents / Conversations
│   └── Hubs/                   # ChatHub、分组名
├── web/                        # 前端（Vue 3 + Vite + Element Plus）
│   ├── vite.config.js          # dev 代理 /api、/hubs → http://localhost:5180
│   └── src/
│       ├── views/              # Login / VisitorChat / AgentConsole
│       ├── components/         # MessageThread（气泡与回执）、ChatComposer
│       ├── lib/                # http（token 拦截器）、api、auth、signalr、chatStore
│       └── router/             # 角色路由守卫
├── tests/ChatServer.Tests/     # xUnit + WebApplicationFactory + SignalR Client
├── desktop/                    # Electron 客服桌面客户端（托盘、系统通知、后端地址可配）
└── README.md
```

## 运行

需要 .NET 8 SDK 与 Node.js 18+（本机 SDK 位于 `~/Desktop/开发/dotnet-sdk`，不在 PATH 时用绝对路径或先 `export PATH`）。

```bash
# 1) 后端（终端 A）
cd ~/Desktop/开发/chatdesk-copy
dotnet build                                  # 0 error
dotnet test                                   # 57 个用例全绿
dotnet run --project ChatServer --urls http://localhost:5180

# 2) 前端（终端 B）
cd ~/Desktop/开发/chatdesk-copy/web
npm install
npm run dev                                   # http://localhost:5173

### 一键启动（macOS）

```bash
cd ~/Desktop/开发/chatdesk-copy
./start.command          # 启动后端 5180 + 访客前端 5173 + 客服前端 5174，并自动打开两个登录页
./start.command stop     # 停止由它启动的进程（前台运行时也可直接 Ctrl+C）
```

访客与客服使用不同端口（5173 / 5174），登录态互不覆盖，可在同一浏览器各开一个标签页分别登录。

```

| 入口 | 地址 | 说明 |
|---|---|---|
| 登录页 | http://localhost:5173/login | 访客登录/注册、客服登录 |
| 访客聊天 | http://localhost:5173/chat | 自动匹配在线客服 + 会话与回执 |
| 客服工作台 | http://localhost:5173/agent | 1 对多会话列表 + 切换回复 |
| 工单记录 | http://localhost:5173/tickets | 客服查看工单聊天记录，转派给其他客服，导出 TXT / CSV |
| 管理后台 | http://localhost:5174/admin | 管理员：仪表盘、客服管理、会话与工单总览、强制转派 |
| 富文本 | 聊天输入框工具栏 | 所见即所得编辑器：粗体/斜体/删除线/行内代码/代码块/引用/列表/链接/表情，按 Markdown 存储并净化渲染 |
| 图片与附件 | 输入框工具栏 | 粘贴/选择图片（≤10 MB，内联显示）、上传附件（≤10 MB，链接下载） |
| 健康检查 | http://localhost:5180/health | 返回 `{"status":"OK"}` |
| Swagger | http://localhost:5180/swagger | 仅开发环境 |

后端不再托管任何页面：直接访问 `http://localhost:5180/` 返回 404 属正常。数据库文件 `ChatServer/chat.db` 在首次启动时自动创建（`EnsureCreated` + WAL）；删除该文件即可重置全部数据。

本机同时演示访客与客服时，请使用两个浏览器（或一个无痕窗口）：两个角色的登录态都保存在 localStorage，同一浏览器里会互相覆盖。右上角「亮色」开关切换明暗主题，选择会被记住。

## 生产部署

前端是纯静态产物，后端只提供 API 与 Hub，两者可分开部署：

```bash
cd web && npm install && npm run build        # 产物在 web/dist
```

把 `web/dist` 交给任意静态服务器（Nginx / Caddy / 静态托管），并配置三点：

1. **反向代理**：`/api` 与 `/hubs`（含 WebSocket 升级）转发到后端 5180；
2. **SPA 回退**：未命中的路径回退到 `index.html`（vue-router 使用 history 模式）；
3. **构建期变量**（可选）：`VITE_API_BASE_URL` 指向后端绝对地址（默认空 = 同源），例如 `VITE_API_BASE_URL=https://chat.example.com npm run build`。

Nginx 参考：

```nginx
server {
    listen 80;
    root /srv/chat-web/dist;

    location / { try_files $uri $uri/ /index.html; }

    location /api/ {
        proxy_pass http://127.0.0.1:5180;
        proxy_set_header Host $host;
    }

    location /hubs/ {
        proxy_pass http://127.0.0.1:5180;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
    }
}
```

生产还需要替换 `Jwt:Key`，并把 `Serilog:FileLogging` 的日志目录纳入运维管理。`dotnet run` 仅用于开发；生产建议 `dotnet publish -c Release` 后以独立进程或容器运行。

## 账号

- 客服种子账号（仅在没有任何客服账号时插入）：`agent1` / `agent2` / `agent3`，密码 `Agent@123456`，展示名分别为 Mia / Leo / Iris，并发上限各 5。旧库中的中文展示名会在启动时自动迁移为英文名。
- 访客：在登录页注册（仅访客可注册，客服账号不开放自助注册），注册成功即返回登录令牌。展示名最长 64 个字符，不接受控制字符。

## 配置项（appsettings.json）

| 键 | 默认值 | 说明 |
|---|---|---|
| `ConnectionStrings:Default` | 空（落 `chat.db`） | 自定义 SQLite 连接串；未写 `Default Timeout` 时自动补 5 秒等锁超时 |
| `Jwt:Key` | 开发用密钥 | HMAC-SHA256 签名密钥，至少 32 字节 |
| `Jwt:Issuer` / `Jwt:Audience` | `ChatServer` / `ChatClient` | 令牌签发者与受众 |
| `Jwt:ExpiryHours` | `8` | 令牌有效期（小时） |
| `Serilog:FileLogging` | `true` | 是否写 `bin/.../logs/log-*.txt` |
| `Retention:Days` | `30` | 聊天记录保留天数（自每条消息发送时起算） |

## HTTP 接口

| 方法 | 路径 | 权限 | 说明 |
|---|---|---|---|
| POST | `/api/auth/register` | 匿名 | 访客注册，返回 token + 用户信息 |
| POST | `/api/auth/login` | 匿名 | 访客/客服共用登录 |
| GET | `/api/auth/me` | 已登录 | 当前账号信息（前端角色守卫） |
| GET | `/api/agents` | 已登录 | 客服列表：在线状态、当前负载、并发上限 |
| POST | `/api/conversations` | 访客 | 创建会话（`agentId` 可空 = 自动分配在线且有余量的最少负载客服）；已存在进行中会话则复用 |
| GET | `/api/conversations/mine` | 已登录 | 我的会话（访客看自己发起的，客服看分配给自己的） |
| GET | `/api/conversations/{id}` | 会话双方 | 会话详情 |
| GET | `/api/conversations/{id}/messages?afterSeq=N` | 会话双方 | 增量补拉：返回 `Seq > N` 的消息，按 Seq 升序，上限 500 |
| POST | `/api/conversations/{id}/close` | 会话双方 | 关闭会话 |
| GET | `/api/tickets` | 客服 | 工单列表（按创建时间倒序） |
| GET | `/api/tickets/{id}` | 客服 | 工单详情与聊天记录 |
| GET | `/api/tickets/{id}/export?format=txt\|csv` | 客服 | 导出工单记录 |
| POST | `/api/files` | 任意登录用户 | 上传图片或附件（multipart，≤10 MB），返回 `/api/files/{id}` |
| GET | `/api/files/{id}` | 匿名 | 读取上传文件；图片内联，其它按附件下载 |
| POST | `/api/tickets/{id}/transfer` | 客服 | 把进行中的工单转派给另一名客服（`{ agentId, note? }`），仅当前服务客服 |
| GET | `/health` | 匿名 | 健康检查 |

状态码：未登录 401；角色不符 / 非会话参与方 403；客服并发已满 409；会话不存在 404；未处理异常统一返回 `{ "error": "..." }`。

## SignalR（`/hubs/chat`）

鉴权用查询参数：`/hubs/chat?access_token=<JWT>`（`/hubs/chat/negotiate` 同理）。

客户端调用：

| 方法 | 说明 |
|---|---|
| `JoinConversation(conversationId)` | 加入会话接收组，返回当前 `status` 与 `lastMessageSeq` |
| `SendMessage(conversationId, content, clientMessageId)` | 发送消息；返回含服务端 `seq` 的回执 |
| `AckDelivered(conversationId, upToSeq)` | 确认收到对端消息 |
| `AckRead(conversationId, upToSeq)` | 确认已读对端消息 |

服务端推送：

| 事件 | 说明 |
|---|---|
| `ReceiveMessage(message)` | 新消息（先落库再广播） |
| `MessageDelivered({conversationId, upToSeq, byAccountId, confirmedAt})` | 对端已送达；只发给确认方之外的连接 |
| `MessageRead({conversationId, upToSeq, byAccountId, confirmedAt})` | 对端已读；只发给确认方之外的连接 |
| `ConversationCreated(conversation)` | 新会话（推给对应客服） |
| `ConversationClosed({conversationId, closedAt})` | 会话被关闭 |
| `ConversationTransferred({conversationId, ticketId, agentId, agentName, transferredAt})` | 工单被转派：推给会话组与原客服组 |
| `ConversationAssigned(conversation)` | 转派后的会话归属目标客服，推给目标客服组 |
| `AgentPresenceChanged({agentId, isOnline, activeConversationCount})` | 客服上下线 |

`byAccountId` 是确认方账号 ID。客户端必须先按它过滤（`byAccountId === 自己` 直接忽略），否则会把自己的消息标成已送达/已读。

## 可靠性设计

1. **先落库后广播**：`ChatService.AppendMessageAsync` 提交成功后才推送 `ReceiveMessage`，崩溃不会产生「收到但没存」的消息。
2. **服务端权威序号**：`Conversation.LastMessageSeq` + 1 分配会话内 Seq；同一会话在进程内串行（带引用计数的信号量闸门，空闲即回收），`(ConversationId, Seq)` 唯一索引兜底，冲突最多重试 5 次。
3. **幂等重发**：`(ConversationId, ClientMessageId)` 唯一索引（非空时）；同一 `ClientMessageId` 重复提交返回原消息（回执 `duplicated: true`），不新增、不重复广播。
4. **断线补拉**：`GET /api/conversations/{id}/messages?afterSeq=N`；重连后前端按本地最大 Seq 补齐，且**循环排空**——一批拉满 200 条就继续拉，直到没有缺口。
5. **三段回执**：发送回执（含 Seq，显示「已发送」）→ 对端 `AckDelivered`（「已送达」）→ 对端 `AckRead`（「已读」）；回执带 `byAccountId` 且不回发确认方自己，前端再按 ID 过滤一次。
6. **按会话的发件箱**：断线时消息进入该会话的发件箱（标记「待发送」），重连后按原 `ClientMessageId` 补发，服务端幂等天然去重；切换会话不会丢别的会话待发消息。
7. **断线兜底**：SignalR 自带重连窗口用尽后，前端按 5 秒间隔继续重连，恢复后重新入组、补拉、补发。
8. **SQLite WAL + 单实例部署**：并发读写下 WAL 更稳，等锁超时由连接串 `Default Timeout=5` 控制（对连接池里每条请求连接生效）；本实现不做多实例集群。

## 测试

```bash
dotnet test          # 57 个用例
cd web && npm run build
```

57 个用例（xUnit + `WebApplicationFactory` + SignalR Client），覆盖：

- 注册/登录/角色鉴权：注册后登录、重复用户名 409、错误密码 401、无令牌 401、客服调用访客接口 403、展示名超 64 字符或含控制字符 400
- `/hubs/chat/negotiate` 无令牌 401、带令牌 200
- 会话：创建 201、同访客同客服复用同一会话、自动分配、客服并发满 409、越权访问 403、关闭后状态为 closed
- 可靠性：20 路并发发送得到严格递增且无缺口的 Seq、重发同 `ClientMessageId` 返回同一消息且只落一行、`afterSeq` 增量返回、关闭会话拒绝新消息、内容去空白与空内容拒绝
- 实时通道：`SendMessage` 回执含 Seq 且广播到对端、重复 `ClientMessageId` 不重复广播、`AckDelivered`/`AckRead` 落库并通知发送方、回执携带 `byAccountId` 且确认方自己收不到自己的回执
- 工单：随会话创建、关闭同步状态、详情按时间正序、TXT/CSV 导出、仅服务客服可见
- 工单转派：归属迁移与台账、非本人/访客/转自己/已结束/满员拒绝、双端事件、原客服不再收到该会话消息
- 管理中台：角色鉴权 401/403、客服增改停用与密码重置、跨客服读会话与工单、强制转派台账含操作人、停用推送 SessionRevoked
- 数据统计：按日计数含 0 值日、首响均值与样本数、按客服维度、days 越界 400
- 图片与附件：上传需鉴权、类型白名单、超 10 MB 拒绝、图片文件头校验、下载还原原文件名
- 数据保留：超过 30 天的消息被清理，工单与会话保留
- 极端网络：客服离线期间的消息落库并在恢复后补拉、客服断线重连后按水位补齐

## 边界（本轮不做）

多实例集群 / Redis 背板、密码找回与刷新令牌、邮箱验证、多设备会话管理、消息撤回编辑、图片与文件消息、排队、账号管理后台。
