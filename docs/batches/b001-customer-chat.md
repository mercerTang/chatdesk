# 2026-10-21 · customer-chat
> 六段 append-only，一段一作者：§1 讨论（主 agent）· §2 批次任务与设计（主 agent）· §3 设计评审（评审子代理）· §4 用户批准（主 agent）· §5 实施记录（实施者：主 agent ∥ coder）· §6 验证与收口（父代理）。
> 编制：主 agent · 2026-10-21 · 来源 = 用户需求：用户能找到对应客服、客服 1 对多、对话可靠性、Web 项目后端 .NET、前端需要登录。
> 台账 = #1（customer-chat · 归批）。前情 = 无（独立批）。
## §1 讨论（主 agent）
**状态行**：🔄 进行中（…）
<§1 模板占位：本批条目 / 关键判据 / 授权口径>
## §2 批次任务与设计（主 agent）
**状态行**：（主 agent 写入时更新）
<§2 模板占位：本批条目（覆盖） / 设计档落点 / 机制设计 / 受影响文件与测试覆盖 / 验收对照 / 关键决策 / 上抛项>

## 批次任务与设计

### 任务
在 `newproject/` 内从零实现客服对话 Web 项目：访客登录后找到对应客服、客服 1 对多接待、对话链路可靠（不丢、不重、不乱序、断线可补），后端 .NET 8。

### 技术选型
- ASP.NET Core 8 Web API + SignalR 实时通道（SDK 8.0.421 已装）
- EF Core + SQLite（WAL），沿用 `dotnet-server` 的保证：Serilog、Controllers、Swagger(仅开发)、CORS
- 前端：静态 HTML/JS + SignalR JS 客户端，无构建链；`wwwroot` 直接由后端托管

### 数据模型
- Account：用户名（唯一）、PBKDF2 密码哈希、展示名、角色（visitor / agent）、在线标记、客服并发上限（默认 5）
- Conversation：访客 ID、客服 ID、状态（active/closed）、LastMessageSeq、LastActivityAt
- Message：会话 ID、会话内单调 Seq、发送者、内容、ClientMessageId、SentAt、DeliveredAt、ReadAt

索引：`(ConversationId, Seq)` 唯一；`(ConversationId, ClientMessageId)` 唯一（非空时），幂等兜底。

### 登录
- JWT（HMAC-SHA256，8 小时），PBKDF2 哈希（Rfc2898DeriveBytes，无外部队列依赖）
- 访客可注册登录；客服仅种子账号（agent1 / agent2 / agent3，密码 Agent@123456），不开放客服自助注册
- 接口按角色鉴权；SignalR 用 access_token 查询参数鉴权
- 种子幂等：仅在无客服账号时插入

### 可靠性机制
1. 先落库后广播：事务提交成功后才推送 SignalR 事件
2. 服务端权威序号：会话内 Seq 单调递增，唯一索引兜底，冲突重试
3. 幂等重发：同一 ClientMessageId 重复提交返回同一条消息，不新增
4. 断线补拉：`GET /api/conversations/{id}/messages?afterSeq=N`，重连后按 lastSeq 补齐
5. 回执：已发送（服务端回执含 Seq）/ 已送达 / 已读
6. 前端跳号检测：收到 Seq 缺口自动补拉
7. SQLite WAL；单实例部署

### 接口
- Auth：register（仅访客）、login、me
- Agents：在线客服列表（在线状态、当前会话数、上限）
- Conversations：创建（指定客服或自动分配最少负载）、我的会话、详情、关闭
- Messages：按 afterSeq 增量拉取
- ChatHub：JoinConversation、SendMessage（返回含 Seq 回执）、AckDelivered、AckRead；客服连接/断开维护在线状态并通知访客端

### 前端
- login.html：访客登录/注册 + 客服登录
- index.html：访客聊天（客服列表、消息状态、断线重连补拉）
- agent.html：客服工作台（1 对多会话列表、切换回复）
- 登录态 localStorage，页面按角色守卫，未登录跳登录页

### 边界（本轮不做）
- 密码找回、刷新 token、邮箱验证、多设备会话管理
- 多实例集群 / Redis 背板、消息撤回编辑、图片文件消息、排队转接
- 真实账号管理后台

### 验收标准
- `dotnet build` 0 error；`dotnet test` 全绿，覆盖：序号单调（并发）、幂等重发、并发上限、增量补拉、鉴权（401/403）
- 启动后 API 冒烟通过；浏览器双端走查：访客发→客服收→客服回→访客收，回执状态正确；刷新/重连后历史与缺口补齐

### 前端技术栈变更（2026-10-21，用户指令）

- 用户要求：改为前后端分离，前端使用 Vue + Element。
- 技术事实：Element UI 只支持 Vue 2；采用 Vue 3 + Vite + Element Plus（当前对应组合）。
- 结构：新增 `web/`（Vue 3 + Element Plus + vue-router + axios + @microsoft/signalr）；Vite dev 代理 `/api`、`/hubs` → `http://localhost:5180`；后端不再托管页面，删除 `ChatServer/wwwroot` 旧页面。
- 前端页面：Login（访客登录/注册 + 客服登录）、VisitorChat（客服列表、会话、消息、三段回执、断线重连、循环补拉、按会话发件箱）、AgentConsole（1 对多会话列表与切换回复、并发上限取接口值）。
- 随本轮修复独立评审发现（详见 §6 验收记录）：回执事件携带确认方 `byAccountId`、扇出不再复用调用方连接令牌、`busy_timeout` 走连接串、`SeqGates` 释放后清理、全局异常处理与请求日志、展示名校验。
- 验收：后端 `dotnet build` / `dotnet test` 全绿；`web` 下 `npm run build` 成功；浏览器走查登录 → 找客服 → 双端收发 → 回执 → 断线补拉；旧静态页删除。
- 影响面：设计文档的前端与 Hub 事件名同步更新；README 改为前后端分离运行方式。

## §3 设计评审（评审子代理）
## §4 用户批准（主 agent）
## §5 实施记录（实施者：主 agent ∥ coder）

## 实施记录（coder · 2026-10-21）

**状态行**：✅ 实施完成

### 落地内容

- 后端 `ChatServer/`（net8.0 Web SDK；Serilog 控制台+文件、EF Core Sqlite 8.0.0、JwtBearer 8.0.0、Swagger 仅开发、CORS AllowAll、`/health`、中文日志），`ChatServer.sln` 聚合后端与测试两个项目。
- 模型 `Account` / `Conversation` / `Message`；索引 `(ConversationId, Seq)` 唯一、`(ConversationId, ClientMessageId)` 唯一且 `ClientMessageId IS NOT NULL`、`(VisitorId, AgentId)` 唯一且 `Status='active'`（创建会话幂等兜底）、`(AgentId, Status)`。
- 口令：内置 `Rfc2898DeriveBytes.Pbkdf2`（SHA256 / 100000 次 / 16 字节盐 / 32 字节密钥），无第三方包。
- 鉴权：JWT HMAC-SHA256 8 小时（`sub` / `name` / `role` / `displayName`）；SignalR 经 `access_token` 查询参数；`MapInboundClaims=false` + 显式 `RoleClaimType="role"`。
- 种子：仅在无客服账号时插入 agent1/agent2/agent3（客服小美/小张/小李，`Agent@123456`，上限 5）；启动 `EnsureCreated` + `PRAGMA journal_mode=WAL` + 在线标记复位。
- `ChatService`：`CreateOrGet`（复用进行中会话 / 指定客服 / 自动分配最少负载 / 满额 409）、`AppendMessage`（先落库、会话内闸门 + 唯一索引冲突重试、`ClientMessageId` 幂等）、`GetMessages(afterSeq, limit)`、`MarkDelivered` / `MarkRead`（ExecuteUpdate）、`Close`。
- `ChatHub`：`JoinConversation`、`SendMessage`（回执含 Seq，落库成功后才广播）、`AckDelivered`、`AckRead`；客服连接/断开维护在线并广播 `AgentPresenceChanged`；客服连上即加入其所有进行中会话。
- REST：`/api/auth/{register,login,me}`、`/api/agents`、`/api/conversations`（POST / `mine` / `{id}` / `{id}/messages` / `{id}/close`）。
- 前端 `wwwroot/`：`login.html` / `index.html` / `agent.html` + `css/site.css` + `js/{api,chat-core,login,visitor,agent}.js`；`js/signalr.min.js` 本地（缺失时回退 CDN）。含角色守卫、客服负载展示、三段回执、跳号补拉、5 秒兜底重连、发件箱按原 `ClientMessageId` 重发。
- 测试 `tests/ChatServer.Tests/`：xUnit + `WebApplicationFactory` + SignalR Client，21 个用例。
- `README.md`：运行方式、账号、配置项、API 与 Hub 事件、可靠性说明、测试覆盖、边界。

### 关键决策与偏差

1. **时间戳统一存 Unix 毫秒**（`ConfigureConventions` + 值转换器）：SQLite 不支持 `DateTimeOffset` 的 `ORDER BY`，实测 `GET /api/conversations/mine` 返回 500 后修正；JSON 仍输出带时区偏移的时间，前端解析不回退。
2. **自动分配在无在线客服时回退到有配额的空闲客服**（排序：在线优先 → 负载最少 → Id 最小）：否则无客服在线时访客无法建会话，与验收「创建会话成功」冲突；指定 `agentId` 时仍严格按配额返回 409。
3. **会话内进程内串行 + 唯一索引重试**：§2 已定「单实例部署」，该组合在不引入分布式锁的前提下保证 Seq 不重、不乱、不跳。
4. **追加兜底重连循环**：SignalR 自带重连窗口约 20 秒，用尽后页面会停在「已断开」。实测 30 秒断服后页面自动恢复并补拉漏收消息，符合「断线补拉」的要求。
5. **正文与边界**：仅新增 `ChatServer/`、`tests/`、`ChatServer.sln`、`README.md`；未改 `docs/`（本段除外）与 `PROJECT-MANIFEST.json`，未触碰 `dotnet-server` / `myBlog`，未执行 git 操作，未引入 npm 构建链。

### 验证

| 项 | 命令 / 方式 | 结果 |
|---|---|---|
| 构建 | `dotnet build`（仓库根，sln 含两个项目） | 0 警告 0 错误 |
| 测试 | `dotnet test` | 21 通过 / 0 失败（含并发序号、幂等重发、并发上限、afterSeq、401/403） |
| 健康检查 | `curl /health` | 200 `{"status":"OK"}` |
| 注册 / 登录 | `POST /api/auth/register`、`POST /api/auth/login`（agent1） | 200 / 200（客服小美·agent） |
| 鉴权 | 无令牌 `/api/agents` 401；客服 `POST /api/conversations` 403；错密码 401 | 符预期 |
| 客服列表 | `GET /api/agents` | 200，三个客服，上限各 5 |
| 会话 | `POST /api/conversations` | 201 创建；重复 200 复用；满额 409 |
| 静态页 | `/login.html`、`/index.html`、`/agent.html` | 均 200 |
| SignalR 协商 | `POST /hubs/chat/negotiate?access_token=…` | 带令牌 200，无令牌 401 |
| 在线状态 | 浏览器登录客服后 `GET /api/agents` → 退出后再查 | `agent1:True` → `agent1:False` |
| 双端走查 | 浏览器：访客发→客服收→客服回→访客收，刷新后回执可复原 | 通过；已发送→已读状态可见 |
| 断线补拉 | 服务停 30 秒 → 断线期间插入一条消息 → 重启 | 页面自动恢复并补出该条消息 |

### 未覆盖

- 浏览器走查为单浏览器会话的分时验证（访客 → 客服 → 访客），双端同时在线只由 SignalR 用例覆盖。
- 并发上限在并发创建下的严格性由创建闸门保证，测试为串行建满 5 个后第 6 个 409。

### 父代理验收修正（2026-10-21）

- 偏差 #2 按 §2 设计收紧：`ChatService.CreateOrGetAsync` 的自动分配现在只挑选 `IsOnline` 且有余量的客服；全部离线时返回 409「当前没有在线且有余量的客服」。对应测试 `Create_WithoutAgentId_AutoAssignsAnAgentWithCapacity` 先把种子客服置为在线再断言分配结果。
- README 同步：自动分配说明改为「在线且有余量的最少负载客服」，并补充同机演示需用两个浏览器（登录态存 localStorage）。
- 复跑：`dotnet build` 0 警告 0 错误；`dotnet test` 21 通过 / 0 失败。curl 实测：全部客服离线时自动分配返回 409，客服控制台在线后同一访客自动分配返回 201（指派 agent1）。

### 前后端分离改造 + 评审修复（coder · 2026-10-21）

#### 落地内容

1. **新增前端 `web/`**（Vue 3.5 + Vite 5 + Element Plus 2.14 + vue-router 4 + axios + @microsoft/signalr 8）：
   - `vite.config.js`：dev 端口 5173，代理 `/api` 与 `/hubs`（`ws: true`）→ `http://localhost:5180`；生产用相对路径，可选 `VITE_API_BASE_URL`。
   - `src/router/index.js`：`/login`（公开）、`/chat`（visitor）、`/agent`（agent）；未登录 → `/login`，角色不符 → 本角色主页。
   - `src/lib/http.js`：axios 请求拦截器带 Bearer token；401 清登录态并跳登录页；错误统一取后端 `{error}`；`src/lib/auth.js`：token + user 存 localStorage。
   - `src/lib/chatStore.js`（访客端与客服台共用）：按会话消息缓存、Seq 去重、ClientMessageId 合并乐观消息、跳号先补拉再落消息、**补拉循环排空**（PAGE_SIZE=200，拉满继续）、三段回执（`byAccountId === 自己` 直接忽略）、**按会话保留发件箱**、SignalR 自动重连 + 5 秒兜底重连，重连后 重新入组 → 补拉 → 补发 → 确认已读。
   - 页面：`Login.vue`（访客登录/注册 + 客服登录，Element Plus 校验：用户名 3-32 无空白、密码 6-64、展示名 ≤64）；`VisitorChat.vue`（客服列表含在线/负载/上限、会话中标记、气泡、回执、结束会话）；`AgentConsole.vue`（1 对多会话列表、未读角标、实时新会话、切换回复、上限取 `/api/agents`）。
   - 组件：`MessageThread.vue`（已发送/已送达/已读/待发送/失败重试）、`ChatComposer.vue`（Enter 发送、Shift+Enter 换行、2000 字上限）。

2. **删除旧静态前端**：`ChatServer/wwwroot/{login,index,agent}.html`、`css/site.css`、`js/{api,chat-core,login,visitor,agent,signalr.min}.js`；`Program.cs` 移除 `UseDefaultFiles` / `UseStaticFiles`。

3. **后端修复**：
   - a. `MessageDelivered` / `MessageRead` 载荷加 `byAccountId`，广播改 `Clients.OthersInGroup` + `CancellationToken.None`；新增测试 `AckRead_CarriesByAccountId_AndConfirmerDoesNotReceiveOwnAck`（断言载荷含 `byAccountId`、确认方连接 0 条回执）。
   - b. 扇出广播全部改 `CancellationToken.None`：ChatHub 的 `ReceiveMessage`、回执、在线状态；控制器 `ConversationCreated`、`ConversationClosed`。
   - c. `busy_timeout` 改连接串 `Default Timeout=5`（`SqliteConnectionStringBuilder`，连接串已显式配置则不覆盖）；删除只对单条连接生效的 `PRAGMA busy_timeout` 并修正注释。
   - d. `ChatService.SeqGates` 改引用计数 `SeqGate`：归还后计数归零即从字典摘除并 Dispose；获取/归还同锁，避免被摘除实例再次取到。
   - e. `Program.cs` 增加 `app.UseSerilogRequestLogging()` 与全局 `UseExceptionHandler`（统一 `{error}`，5xx 记日志不暴露堆栈，`BadHttpRequestException` 保留原状态码）。
   - f. 注册展示名限制 ≤64 且拒绝控制字符（空值回退用户名）。

4. **`README.md`** 重写为前后端分离的运行 / 生产部署（Nginx 参考）/ 账号 / 配置 / 接口 / Hub 事件（含 `byAccountId`）/ 可靠性 / 测试 / 边界说明。

#### 关键决策与偏差

1. 回执采用 **OthersInGroup + byAccountId 双保险**：确认方自己的连接不收回执；同一账号的第二个连接由前端按 `byAccountId` 过滤。前端两处过滤都实现，禁止把自己的消息标成已读/已送达。
2. 删除 `wwwroot` 后必须先 `dotnet clean` 再构建：Web SDK 的 staticwebassets 清单仍引用旧目录，直接运行会 `DirectoryNotFoundException`；clean 重建后清单为空、启动正常。
3. Element Plus 全量引入：主 chunk ~1.06 MB（gzip 347 KB），构建有 chunk 体积警告但不报错；本轮不引入按需导入插件（不做额外工程化）。
4. 前端不引入 Pinia / TypeScript / 测试框架：状态用 `reactive`，组件用 `<script setup>`。
5. 未新增后端接口；前端只消费既有 REST 与 Hub 契约。

#### 验证

| 项 | 命令 / 方式 | 结果 |
|---|---|---|
| 后端构建 | `dotnet build ChatServer.sln` | 0 警告 0 错误 |
| 后端测试 | `dotnet test ChatServer.sln` | 23 通过 / 0 失败（21 原有 + 回执身份 + 展示名校验） |
| 前端构建 | `cd web && npm run build` | 成功，生成 `dist/index.html` 与 `dist/assets/*` |
| 健康检查 | `curl :5180/health` | 200 `{"status":"OK"}` |
| 旧静态页 | `curl :5180/login.html`、`curl :5180/` | 404 / 404（后端不再托管页面） |
| 前端→API | `curl :5173/api/agents`（无 token）、`POST :5173/api/auth/login`（agent1） | 401（代理可达后端）/ 200 返回 token |
| 登录页渲染 | 浏览器 `:5173/login` | 访客/客服 Tab、校验规则、登录/注册切换正常 |
| 访客闭环 | 浏览器注册 visitor_demo → 选客服小美 → 发消息 | 落库 seq=1，气泡「已发送」 |
| 客服回执 | Node SignalR 对端脚本以 agent1 连接 → AckDelivered/AckRead → 回复 | 访客端消息变「已读」，收到回复 seq=2 |
| 客服工作台 | 浏览器登录 agent1 → `/agent` | 会话列表、实时更新、回复；自身消息「已读」；「同时接待上限 5 位」取自 `/api/agents` |
| 断线发件箱 | 停后端 30s+ → 访客页发消息 | 显示「待发送（断线）」；重启后按原 ClientMessageId 补发成功并变「已读」 |
| 断线补拉 | 客服端 08:07:45.686 发出 seq 8/9，访客连接 08:07:45.830 才恢复 | 恢复后页面未刷新即出现 seq 8/9（状态「重连中」→「已连接」），经 afterSeq 循环补拉 |
| 回执身份 | 新增用例 | 通过：`byAccountId` 正确，确认方自己 0 条回执 |

#### 未覆盖 / 说明

- 浏览器走查在同一浏览器会话内分时完成（访客 UI 与客服脚本/客服 UI 交替），双端同时在线由 Node SignalR 对端脚本 + 浏览器组合覆盖。
- 跳号补拉（收到 `seq > lastSeq + 1` 的分支）未单独构造缺口触发；它和断线补拉共用同一 `pull()` 实现，断线补拉与循环排空已实测。
- `web/node_modules` 与 `web/dist` 为依赖/构建产物，已在 `web/.gitignore` 排除。

#### 收尾自检修正（同上轮次，2026-10-21）

- `ChatComposer.vue`：Enter 发送增加输入法组合判断（`event.isComposing || keyCode === 229` 时不发送），中文选词按 Enter 不再误发；实测回车发送仍正常（消息落库并显示「已发送」）。
- `Login.vue`：`el-radio-button` 由 `label` 改为 Element Plus 2.x 的 `value` 写法，消除废弃告警；实测登录/注册切换正常。
- `chatStore.send`：确认「会话已关闭」判断在创建乐观消息之前，关闭态不会留下悬挂的「发送中」气泡。
- 修正后复跑：`npm run build` 成功；`:5180/health` 200、`:5173/login` 200；浏览器登录 visitor_demo 发送消息成功。

### 父代理收尾修正（2026-10-21）

- 已读语义修正：`chatStore` 只在会话处于前台（`open()` 设置 visibleId）时发 `AckRead`；后台会话收到消息只发 `AckDelivered`。实测：客服停留在会话 A 时，会话 B 的新消息 `ReadAt` 为空、`DeliveredAt` 有值；点开会话 B 后 `ReadAt` 才落库。
- 未打开会话的送达确认：收到 `ReceiveMessage` 但本地无该会话缓存时，直接按 `dto.seq` 补发 `AckDelivered`，避免「已送达」漏报。
- 评审遗留 🔵 清理：`ChatHubTests` 的两处 `Task.Delay` 改为往返屏障/直接断言；删除无人使用的 `AgentPresenceService.IsOnline`、`OnlineAgentIds`、`AppendResult.AgentId/VisitorId`、`AccountUserIdProvider` 注册与文件；前端删除未使用的 `setVisible` 导出与 `api.getConversation`。
- 复跑：`dotnet build` 0 警告 0 错误；`dotnet test` 23 通过 / 0 失败；`npm run build` 成功。

### 父代理收尾修正（续，2026-10-21）

- 访客只保留一个进行中的会话：服务端自动分配改为仅在线客服、无可用 409；新增 `(VisitorId)` 活跃唯一索引；访客页移除客服/好友列表，登录后直接匹配一位客服。
- 客服工作台按访客去重：`/api/conversations/mine` 对客服角色同一访客只返回一行（进行中优先），前端再做一次冗余去重；新会话按 `visitorId` 替换而不是追加。
- 聊天记录本地持久化：新增 `web/src/lib/localDb.js`（IndexedDB）；打开会话先读缓存渲染，再按缺口向服务端对账，缓存缺段时从 0 补拉。
- 视觉重做：月之暗面/月之亮面双主题（右上角「亮色」开关，localStorage 记忆）；Kimi 品牌蓝 #007CFF 为唯一强调色，状态色统一蓝/灰；登录页月盘主视觉、月盘漂浮与光晕漂移、文字/消息入场、页面切换，尊重 reduced-motion。
- Logo：取 myBlog 中的 GitHub 头像（`github.com/mercerTang.png`）落地为 `web/public/logo.jpg`，做圆形头像 + 蓝色轨道环 + 字标的品牌锁定。
- 客服展示名改英文：种子 agent1/agent2/agent3 → Mia/Leo/Iris；旧库中文名在启动时一次性迁移（仅旧名，不覆盖自定义）。
- 验证：`dotnet test` 25 通过 / 0 失败；`npm run build` 通过；浏览器实测访客自动匹配、单会话限制、客服列表去重、主题开关与两套配色。

### 父代理收尾修正（续二，2026-10-21）

- 新消息提醒：客服在非当前会话收到访客消息时，右下角弹出玻璃通知（访客名 + 内容摘要）并累加未读徽标；标签页不可见时标题闪烁「新消息」，回到页面自动停止。
- 排序：聊天窗口最新消息排在最上面（未确认消息视为最新置顶）；客服左侧会话列表按最后消息时间倒序，新消息到达即置顶。
- 空态图标：由蓝色圆点换成品牌头像（圆形 + 轨道环）。
- 输入框：改为悬浮在消息区上方的磨砂玻璃面板，暗/亮主题各有一套玻璃参数。
- 工单记录：会话创建时自动生成工单（`Ticket`，工单号 `T-YYYYMMDD-NNNNN`），关闭时同步状态；客服在 `/tickets` 查看自己会话的完整聊天记录（时间正序），支持导出 TXT / CSV；已有库启动时补建 `Tickets` 表并为历史会话补建工单。
- 验证：`dotnet test` 28 通过 / 0 失败；`npm run build` 通过；curl 实测工单列表、详情、TXT/CSV 导出（含 Content-Disposition 与中文内容）；浏览器实测工单页列表与详情、导出按钮。

### 数据保留（2026-10-21）

- 新增 `RetentionService`：关闭的会话以 `ClosedAt`（缺失用 `LastActivityAt`）为准，超过 `Retention:Days`（默认 30）后连同消息与工单一起清理；进行中的会话不清理。
- 新增 `RetentionHostedService`：启动执行一次，之后每 6 小时执行；异常只记日志，不影响服务。
- 配置 `appsettings.json` 增加 `Retention:Days = 30`；`GET /api/tickets/meta` 返回保留天数，工单页展示「关闭后保留 30 天」。
- 浏览器 IndexedDB 缓存同样按 30 天清理（`purgeOlderThan`，创建聊天引擎时触发）。
- 验证：`dotnet test` 29 通过 / 0 失败（新增保留期清理用例）；`npm run build` 通过；`/api/tickets/meta` 实测返回 `{"retentionDays":30}`。

### 数据保留修正（2026-10-21）

- 用户澄清：工单里的聊天记录自**每条消息发送时间**起 30 天过期、逐条删除；工单与会话本身保留，不随过期删除。
- `RetentionService` 改为按 `SentAt < now - Retention:Days` 删除消息；`RetentionHostedService` 维持启动一次 + 每 6 小时一次。
- 前端工单页提示改为「聊天记录自发送起保留 30 天」；IndexedDB 缓存同样按消息时间清理。
- 验证：`dotnet test` 29 通过；真实库插入一条 31 天前的消息并重启，启动清理后该消息删除、工单（10 张）与近期消息（7 条）保留。

### 消息顺序最终口径（2026-10-21）

- 右侧聊天窗口：按 Seq 升序展示，旧消息在上、新消息在下，新消息到达后自动滚到底部（覆盖此前「最新在上」的临时调整）。
- 左侧客服会话列表：仍按最后消息时间倒序，最新会话在最上面。
- 工单详情：按 Seq 升序展示聊天记录，与聊天窗口一致。
- 过期消息只展示剩余部分：服务端查询/导出/工单统计按 `SentAt >= 截止时间` 过滤，客户端也按消息时间过滤并跳过写入本地缓存。

### 极端网络测试（2026-10-21）

- 新增 `NetworkResilienceTests`（2 例）：客服从未连接时访客连发 3 条，客服上线后按 afterSeq 全部补齐；客服连接后断开，访客再发，重连后按水位补拉。
- 真实演示：关闭客服页面模拟掉线 → 以访客身份发送 2 条 → 重启后端 → 重新打开客服页，两条消息全部补齐（消息在库中，序号 16/17）。
- 访客侧断网：停后端后发送 → 界面显示「待发送（断线）」进入发件箱 → 后端恢复后按原 ClientMessageId 补发 → 数据库中仅 1 行（seq 19），无重复。
- 验证：`dotnet test` 31 通过 / 0 失败；`npm run build` 通过；服务已重新启动。
- 已知边界：访客发件箱目前只在内存中，断线期间若刷新/关闭页面，未发出的排队消息会丢失；服务端已落库的消息不受影响。需要时可将发件箱持久化到 IndexedDB。

## §6 验证与收口（父代理）
