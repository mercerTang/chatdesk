# 2026-10-22 · admin-console

> 六段 append-only，一段一作者：§1 讨论（主 agent）· §2 批次任务与设计（主 agent）· §3 设计评审（评审子代理）· §4 用户批准（主 agent）· §5 实施记录（实施者）· §6 验证与收口（主 agent）。
> 编制：主 agent · 2026-10-22 · 来源 = 用户需求：加客服桌面客户端、中台与数据统计（本批为中台）
> 台账 = #2（customer-chat · 归批）。前情 = b002-ticket-transfer（工单转派，已收口）。
> 批档写入通道说明：本会话锚点在用户主目录，批档基址指向 `newproject`，批档工具拒绝跨仓创建与写入；本批六段由主 agent 直接写入本仓，§5 依据实施者报告与主 agent 复核代录。

## §1 讨论（主 agent）

**状态行**：已收口 2026-10-22

本批条目：管理中台（管理后台）——新增管理员角色与后台页面，用于管客服账号（新增、改展示名与并发上限、启用停用、重置密码）、跨客服查看会话与工单、强制转派工单。

关键判据：

- 现有角色只有 `visitor` / `agent`（`ChatServer/Models/Constants.cs:6-7`），鉴权、路由、落地页都按这两个值分流；加 `admin` 必须同时改这三处，否则管理员登录后会被踢回访客页。
- 账号没有启用/停用字段（`ChatServer/Models/Account.cs:4-27`），需要新增列；已有库在启动时按 `PRAGMA table_info` 检测补列，不能重建库。
- 工单与会话接口一律按「当前客服名下」过滤（`ChatServer/Controllers/TicketsController.cs:82-86`），中台需要独立的跨客服读接口，不能复用客服端接口放宽。
- 强制转派复用 `ChatService.TransferTicketAsync` 的规则与组迁移，只跳过「必须是当前服务客服」这一条；转派台账记录原客服，管理员身份另记 `OperatorId`，保证审计可查。
- 停用客服不硬断连接：禁止新登录，并向其在线连接推 `SessionRevoked`，前端登出到登录页（避免打断进行中的会话）。

授权口径：用户 2026-10-22 回「可以」，批准三批方案（中台 → 统计 → 桌面客户端）与四条口径（管理员种子账号、停用不硬断、管理员可强制转派、界面叫「管理后台」）。

## §2 批次任务与设计（主 agent）

**状态行**：设计完成

### 任务

管理员登录后能：管客服账号、看全部会话与工单、把任意进行中的工单强制转派给指定客服。访客与客服看不到也调不动这些能力。

### 数据模型

- `Account.IsActive`（bool，默认 true）：停用后不能登录；老库补列。
- `TicketTransfer.OperatorId`（int?，可空）：管理员强制转派时记录操作人；客服自助转派为空。老库补列。
- 种子账号 `admin` / `Admin@123456`（角色 admin，幂等：已存在则不写）。

### 接口（`[Authorize(Roles = admin)]`，前缀 `/api/admin`）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/agents` | 全部客服：在线、活跃会话数、上限、启用状态、创建时间 |
| POST | `/agents` | 新增客服：`{ username, displayName, password, maxConcurrentConversations? }` |
| PATCH | `/agents/{id}` | 改 `displayName` / `maxConcurrentConversations` / `isActive` / `password`（均可选） |
| GET | `/conversations` | 全量会话，筛选：`status` / `agentId` / `visitorId` / `from` / `to` |
| GET | `/tickets` | 全量工单，筛选：`status` / `agentId` / `from` / `to` |
| GET | `/tickets/{id}` | 任意工单详情（含聊天记录与转派台账） |
| POST | `/tickets/{id}/transfer` | 强制转派：`{ agentId, note? }`，跳过「必须是当前客服」 |

登录接口沿用 `/api/auth/login`；`IsActive = false` 返回 403「账号已停用」。停用时向 `agent-{id}` 组推 `SessionRevoked`，前端清会话并回登录页。

### 前端

- 登录页新增「管理员登录」表单（与访客、客服并列）。
- `/admin` 区域：侧栏布局（仪表盘 / 客服管理 / 会话总览 / 工单总览 / 返回工作台）。
- 客服管理：表格 + 新增对话框 + 行内编辑（展示名、并发上限、启用停用、重置密码）。
- 会话总览：表格（访客、客服、状态、最后消息、时间）+ 筛选。
- 工单总览：表格（工单号、访客、客服、状态、消息数、时间）+ 详情抽屉（聊天记录 + 转派台账）+ 强制转派按钮。
- 路由守卫与 `homePath` 支持 admin。

### 受影响文件

后端：`Models/Constants.cs`、`Models/Account.cs`、`Models/TicketTransfer.cs`、`Data/AppDbContext.cs`、`Services/DatabaseInitializer.cs`、`Services/ChatService.cs`（管理员转派方法）、`Controllers/AuthController.cs`（停用拒绝）、`Controllers/TicketsController.cs`（抽取可复用的组迁移）、`Controllers/AdminController.cs`（新）。
前端：`router/index.js`、`lib/auth.js`、`lib/api.js`、`lib/chatStore.js`、`views/Login.vue`、`views/admin/AdminLayout.vue`、`AdminDashboard.vue`、`AdminAgents.vue`、`AdminConversations.vue`、`AdminTickets.vue`（新）。
测试：`tests/ChatServer.Tests/AdminApiTests.cs`（新）。

### 验收对照

- 访客、客服调用 `/api/admin/*` 一律 403；未登录 401。
- 新增客服可登录；重复用户名 409；弱密码 400；停用后登录 403，启用后恢复。
- 管理员列表能看到 agent1 与 agent2 名下的工单；任意工单详情可读。
- 强制转派：工单归属迁移、原客服列表消失、台账 `FromAgentId` = 原客服且 `OperatorId` = 管理员。
- `dotnet build` 0 错误、`dotnet test` 全绿、`npm run build` 通过。

### 关键决策

- 最接近的先例：工单转派（b002）与客服列表（`AgentsController`）。对齐：DTO 投影复用、`ChatException` → HTTP 语义、组事件推送；偏离：中台读接口放宽到跨客服，故独立控制器与角色。
- 停用不硬断：用 `SessionRevoked` 事件让前端登出，避免打断进行中的会话；硬断留作后续。
- 管理员转派单独方法：不让「跳过归属校验」成为客服路径的可选项，避免误用扩大权限。

### 调查证据

已核对角色常量、账号模型、鉴权与路由分流、工单归属过滤、转派服务与组迁移代码（坐标见上）；不引入新依赖。

## §3 设计评审（评审子代理）

来源：独立 advisor 评审（type=code，范围 AdminController / ChatService / ConversationRealtimeService / DatabaseInitializer），结论 **VERDICT: changes-required**（1 条必修 + 5 条非必修），本段由主 agent 代录。

| # | 位置 | 级别 | 发现 | 处置 |
|---|---|---|---|---|
| 1 | `ChatService.cs:124` / `:83-84` / `:253-255`；`ChatHub.cs:40` | 🟡 必修 | 停用只在登录入口生效：自动分配、访客显式指定、转派目标都不校验 `IsActive`；停用前的 token 仍可重连 Hub 继续收发 | 已修：自动分配加 `IsActive`；显式指定与转派目标按「客服不存在」拒绝；`OnConnectedAsync` 拒绝停用账号建连并 Abort；`RequireAccountAsync` 对停用客服抱 403；新增用例锁口径 |
| 2 | `DatabaseInitializer.cs:43` | 🟡 可选 | 同名 `admin` 被访客占用时永远不会创建管理员且无提示 | 已修：检测到占用者非 admin 时输出错误日志 |
| 3 | `AdminController.cs:29` | 🔵 | 统计窗口 90 天大于消息保留 30 天，超过保留期的消息数与首响样本偏低；转派会话的首响记在当前归属客服 | 不修（口径记录）：在设计档「数据统计」写明保留期对统计的影响与首响归属口径 |
| 4 | `AdminController.cs:264-281` / `:434-459` | 🔵 | 列表与统计无分页，全量拉窗口内消息；会话数超 SQLite 绑定变量上限会查询失败 | 不修：演示规模可接受，作为扩展点记在 b004 §5 |
| 5 | `DatabaseInitializer.cs:13` | 🔵 | 种子管理员密码硬编码，生产拿到源码即拿到最高权限 | 已在设计档与 README 注明生产必须改密 |
| 6 | `ChatService.cs:282-285` | 🔵 | 转派日志固定写「操作管理员」，客服自助转派时文案误导 | 已修：按 operatorId 是否为空分别输出「客服自助」/「操作管理员」 |

其余核对项均通过：admin 角色与 JWT `role` 链路一致、DTO 与投影逐字段对齐、`ConversationRealtimeService` 已注册、转派与创建共用全局闸门、`IsActive` 补列对老库有默认值 1、日桶边界与本地日换算正确、`Error()` 覆盖全部错误码。

## §4 用户批准（主 agent）

**状态行**：已批准

- 2026-10-22：用户对三批方案回「可以」，四条口径（`admin` / `Admin@123456`、停用不硬断、管理员可强制转派、界面叫「管理后台」）随方案一并批准。

## §5 实施记录（实施者）

来源说明：本段由主 agent 依 coder 子代理实施报告与逐文件复核代录。

### 落地范围

后端：`Models/Constants.cs`（admin 角色）、`Models/Account.cs`（IsActive）、`Models/TicketTransfer.cs`（OperatorId）、`Data/AppDbContext.cs`、`Dtos/AdminDtos.cs`（新）、`Dtos/TicketDtos.cs`（TicketDto 加 AgentId）、`Services/DatabaseInitializer.cs`（补列 + admin 种子）、`Services/ChatService.cs`（AdminTransferTicketAsync）、`Services/ConversationRealtimeService.cs`（新，转派通知抽取）、`Controllers/AdminController.cs`（新）、`Controllers/AuthController.cs`（停用拒绝）、`Controllers/TicketsController.cs`（改用通知服务）。
前端：`router/index.js`、`lib/auth.js`、`lib/api.js`、`lib/chatStore.js`（SessionRevoked）、`views/Login.vue`（管理员表单）、`views/admin/`（布局 + 四个页面）、`styles.css`。
测试：`tests/ChatServer.Tests/AdminApiTests.cs`（6 例）。

### 关键实现

- `IsActive` 与 `OperatorId` 用 `PRAGMA table_info` 检测后原生 SQL 补列，老库无需重建。
- 种子 admin / Admin@123456；停用账号登录 403「账号已停用」。
- 强制转派 `AdminTransferTicketAsync` 跳过归属校验，台账 `FromAgentId` = 原客服、`OperatorId` = 管理员；组迁移复用 `ConversationRealtimeService`（客服端转派同步改造）。
- 停用推送 `SessionRevoked`，不硬断连接。

### 与设计点的偏差

- 通知服务命名为 `ConversationRealtimeService`（设计点写的是 TransferNotifier），职责相同。
- 仪表盘在同批内先做了指标卡与最近工单（原计划留到统计批），统计接口接入后由 b004 接管。

## §6 验证与收口（主 agent）

### 验证读数

- `dotnet build`：0 错误；`dotnet test`：52/52 通过（b003 落地时 46，评审修复后扩到 52）。
- `npm run build`：成功。
- 接口冒烟：admin 登录 200 + role=admin；`GET /api/admin/agents` 返回 3 个客服及在线/负载；`GET /api/admin/tickets` 返回全部客服名下共 4 张工单。
- 界面：管理后台四个页面与强制转派对话框实测可用，截图见 `tests/screenshots/01`–`05`。

### 收口结论

R8 管理中台落地：admin 角色与后台隔离、客服账号全生命周期、跨客服读、强制转派（含操作人台账）、停用推送登出。

遗留：数据统计（b004）与桌面客户端（b005）另批。
