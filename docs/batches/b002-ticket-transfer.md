# 2026-10-22 · ticket-transfer

> 六段 append-only，一段一作者：§1 讨论（主 agent）· §2 批次任务与设计（主 agent）· §3 设计评审（评审子代理）· §4 用户批准（主 agent）· §5 实施记录（实施者）· §6 验证与收口（主 agent）。
> 编制：主 agent · 2026-10-22 · 来源 = 用户需求：客服加一个工单转派功能，可以把用户的 ticket 转给另一个客服
> 台账 = #1（customer-chat · 归批）。前情 = b001-customer-chat（同一系统的上一批，已收口）。
> 批档写入通道说明：本会话锚点在用户主目录，批档基址指向 `newproject`，批档工具拒绝跨仓创建与写入；本批六段由主 agent 直接写入本仓，§5 依据实施者报告与主 agent 复核代录。

## §1 讨论（主 agent）

**状态行**：已收口 2026-10-22

本批条目：R7 工单转派 —— 客服把名下进行中的工单转给另一名客服，工单与会话一并交接，原客服失去访问与实时接收，访客看到客服变更，转派留痕。

关键判据：

- 工单归属由 `Conversation.AgentId` 决定（`ChatServer/Controllers/TicketsController.cs:82-86`），转派即改归属，工单列表、详情、导出随之跟随，不需要给 Ticket 增加客服字段。
- 交接必须收回原客服的实时接收权：会话组投递没有服务端撤销入口（`ChatServer/Hubs/ChatHub.cs:38-66`），本轮在转派时由服务端移动组员。
- 只支持进行中工单；已结束工单不可转派（用户 2026-10-22 确认）。
- 目标客服满员拒绝、离线放行并在界面标注（默认口径，用户未推翻）。

授权口径：用户 2026-10-22 在下发需求后，对两条口径回「2」确认第二条，其余按默认执行。

## §2 批次任务与设计（主 agent）

**状态行**：设计完成

### 任务

客服在「工单记录」页把一张进行中的工单转派给另一名客服。转派后由目标客服接续服务：原客服不再看到该工单、也收不到该会话的实时消息；访客端即时看到客服变更；每次转派留痕，可在工单详情查看。

### 数据模型

新增 `TicketTransfer`（转派台账）：`Id`、`TicketId`（外键，级联删除）、`FromAgentId` / `ToAgentId`（外键，限制删除）、`Note`（可空，≤200）、`CreatedAt`；索引 `(TicketId, CreatedAt)`。已有库在启动时按 Tickets 表的同一范式补建（`ChatServer/Services/DatabaseInitializer.cs:49-72`）。

### 接口

`POST /api/tickets/{id}/transfer`，body `{ agentId, note? }`，仅 agent 角色，返回更新后的 `TicketDto`：

| 情况 | 结果 |
|---|---|
| 调用者不是该工单当前客服 | 404 工单不存在（不泄露存在性，沿用 `TicketsController.cs:101-108` 口径） |
| 目标不存在或非客服 | 404 客服不存在 |
| 目标是自己 | 400 不能转派给自己 |
| 工单已结束 | 409 工单已结束，无法转派 |
| 目标活跃会话数 ≥ 上限 | 409 目标客服会话已满（复用 `ChatService.EnsureCapacity`，`ChatServer/Services/ChatService.cs:449-457`） |
| 正常 | 改 `Conversation.AgentId`、刷新 `LastActivityAt`、写一条转移记录，单次 SaveChanges |

并发保护：复用创建侧的全局闸门（`ChatService.cs:24`），避免两次转派同时击穿同一客服的并发上限。

### 实时投递

转派提交后由服务端完成组员迁移，不依赖前端配合：

- 原客服的在线连接移出 `conversation-{id}` 组；目标客服的在线连接加入该组。连接 ID 由 `AgentPresenceService` 从「计数」升级为「连接集合」记录。
- 先向会话组与原客服组推 `ConversationTransferred{conversationId, ticketId, agentId, agentName, transferredAt}`，再把目标客服并入组并推 `ConversationAssigned(conversation)`。
- 原客服之后调用 `JoinConversation` 也会被成员校验拒绝（`ChatService.LoadConversationForAsync`），组身份与接口权限同时收回。

### 受影响文件

后端：`Models/TicketTransfer.cs`（新）、`Data/AppDbContext.cs`、`Dtos/TicketDtos.cs`、`Services/ChatService.cs`、`Services/AgentPresenceService.cs`、`Services/DatabaseInitializer.cs`、`Hubs/ChatHub.cs`、`Controllers/TicketsController.cs`。
前端：`web/src/lib/api.js`、`web/src/lib/chatStore.js`、`web/src/views/Tickets.vue`、`web/src/views/AgentConsole.vue`、`web/src/views/VisitorChat.vue`。
测试：`tests/ChatServer.Tests/TicketTransferTests.cs`（新）。

### 验收对照

- 转派后：原客服工单列表不再含该工单、详情 404；目标客服列表出现该工单、详情可读。
- 转派记录在详情按时间正序返回。
- 拒绝路径：非本人 404、访客 403、已结束 409、目标满员 409、转给自己 400、目标不存在 404。
- 实时：原客服连接收到 `ConversationTransferred` 且此后收不到该会话新消息；目标客服连接收到 `ConversationAssigned` 并能收到该会话新消息。
- `dotnet build` 0 错误、`dotnet test` 全绿、`cd web && npm run build` 通过。

### 关键决策

- 最接近的先例：会话关闭（`ChatService.CloseAsync` + `ConversationsController.Close`）。对齐：成员校验、状态校验、`ChatException` → HTTP 语义、变更事件推送；偏离：转派还要处理第二参与者的并发上限与组身份迁移，故新增独立服务方法与两个事件。
- 组员迁移而非改造投递：把消息投递整体改为按账号投递，会动到序号、幂等、回执的可靠性链路；本轮只让在线状态记录连接 ID，转派时做组员迁移，改动面限于在线状态与转派路径。
- 工单不加客服字段：归属继续由会话表达，避免同一事实两处存储。

### 调查证据

已核对本项目需求档、设计档、相关代码与既有测试（坐标见上）；本批不引入新依赖或第三方接口，无需外部一手来源。

## §3 设计评审（评审子代理）

来源说明：评审由独立 advisor 子代理完成（type=code，范围：`ChatService.cs` + `TicketsController.cs`，依赖追溯覆盖 presence/hub/DTO/DI/前端事件契约），本段由主 agent 依评审原文代录。前两次评审因范围过大在评审模型侧超时，第三次收窄到两个文件后产出结论。

| # | 位置 | 级别 | 发现 | 处置 |
|---|---|---|---|---|
| 1 | `TicketsController.cs:149` | 🟡 | 数据库提交后的组迁移与广播若抛异常，已生效的转派会返回 500，且原客服可能仍留在会话组 | 已修：包进 try/catch 并记日志，失败不再影响响应 |
| 2 | `ChatService.cs:249` | 🔵 | 为日志重查 fromAgent，多一次闸门内往返；兜底分支不可达且消息有误导 | 已修：改用入参 `fromAgentId`，删除该查询 |
| 3 | `ChatService.cs:227` | 🔵 | 校验顺序：已结束工单自转派返回 400 而非 409 | 已修：状态检查前移到归属校验之后 |
| 4 | `ChatService.cs:208` | 🔵 | 转派（全局闸门）与发消息（会话 SeqGate）不互斥，在途消息可跨转派落库 | 不修：固有 TOCTOU，发送时发送者仍是合法参与者；加校验会提高核心链路复杂度 |

评审结论原文：VERDICT: pass（未发现 🔴 或必须修复项）。

## §4 用户批准（主 agent）

**状态行**：已批准

- 2026-10-22：用户下发需求「加一个工单转派的功能，可以把用户的 ticket 转给另一个客服」；主 agent 给出方案与两条待确认口径，用户回「2」，确认第二条（只支持进行中工单；满员拒绝、离线放行），其余按默认执行，视为批准开工。
- 批准范围：数据模型、转派接口、实时组员迁移、前端三处交互、转派测试。不包含排队、批量转派、客服台内转派入口、访客主动换客服。

## §5 实施记录（实施者）

来源说明：本段由主 agent 依 coder 子代理的实施报告与逐文件复核代录（本会话批档写入通道跨仓不可用）。

### 落地范围

后端：`Models/TicketTransfer.cs`（新）·`Data/AppDbContext.cs`（DbSet、外键、索引）·`Dtos/TicketDtos.cs`（`TicketTransferDto`、`TransferTicketRequest`、`TicketDetailDto.Transfers`、共享投影 `TicketProjections`）·`Services/ChatService.cs`（`TransferTicketAsync`、`LoadTicketDtoAsync`、`TransferTicketResult`）·`Services/AgentPresenceService.cs`（连接 ID 集合）·`Services/DatabaseInitializer.cs`（补建表与索引）·`Hubs/ChatHub.cs`（传 `Context.ConnectionId`）·`Controllers/TicketsController.cs`（转派接口、详情带台账、组员迁移与事件）。
前端：`lib/api.js`（`transferTicket`）·`lib/chatStore.js`（两个事件、`forget`）·`views/Tickets.vue`（转派按钮、对话框、台账展示）·`views/AgentConsole.vue`（转入/转出处理）·`views/VisitorChat.vue`（客服名变更）。
测试：`tests/ChatServer.Tests/TicketTransferTests.cs`（新，9 例）。

### 关键实现

- `ChatService.TransferTicketAsync(int ticketId, int fromAgentId, int targetAgentId, string? note, CancellationToken)` 在 `CreateGate` 内完成校验、改 `Conversation.AgentId`、刷新 `LastActivityAt`、写台账，单次 SaveChanges；结果 `TransferTicketResult(TicketDto, ConversationDto, FromAgentId, TransferredAt)`。
- 控制器顺序：移出原客服连接 → 向会话组与原客服组推 `ConversationTransferred` → 并入目标客服连接 → 向目标客服组推 `ConversationAssigned`。组操作走 `IHubContext.Groups`，未引入新依赖，`Program.cs` 未改。
- 校验顺序：归属（非本人 404）→ 转给自己 400 → 目标存在且为客服 404 → 工单已结束 409 → 目标容量 409 → 备注长度 400。

### 与设计点的偏差

- 任务书允许「按连接 ID 迁移组」，实现用 `IHubContext<ChatHub>.Groups`（等价且更简洁），`AgentPresenceService` 的读取方法命名为 `ConnectionsOf`。
- 工单投影从控制器私有方法抽到 `Dtos/TicketDtos.cs` 的 `TicketProjections`，供列表、详情、转派响应三处共用（比对设计点：新增的共享投影，消除三份重复）。
- 前端目标客服下拉对「已满」项置灰（任务书要求只在界面标注、由后端拒绝）。后端 409 仍然生效；置灰用的是打开对话框时拉取的最新负载，属提前提示，不改变拒绝口径。
- `Services/ChatModels.cs` 曾临时加入结果记录后移回 `ChatService.cs`，最终内容与原文件一致，无净差异。

## §6 验证与收口（主 agent）

### 验证读数

- `dotnet build`：0 错误，2 个警告（均为既有 `TicketApiTests.cs` 的 xUnit2031）。
- `dotnet test`：40/40 通过（既有 31 + 新增 9），修复轮后重跑仍 40/40。
- `cd web && npm run build`：成功，产物 `web/dist`（仅既有的 chunk 体积告警）。
- `doc_check gate`：需求档、设计档、README 均 0 命中（批次目录按声明域不参与）。
- 界面闭环（隔离端口，临时库，未触碰用户原有服务）：后端 5182 + 前端 5175；agent1 把进行中工单转给 Leo 后列表立即少一条、转派按钮变灰；agent2（Leo）登录看到「王小二 → Leo · 进行中」，详情展示台账「Mia → Leo · 订单问题请 Leo 接手」。验收后临时服务已停止、临时文件已删除。
- 自动化覆盖原客服断流：`Transfer_RealTime_MovesGroups_AndOldAgentStopsReceiving` 断言原客服 `JoinConversation`/`SendMessage` 被拒且收不到新消息（以目标客服收到消息作为时间屏障）。

### 收口结论

R7 工单转派落地：改归属即改工单归属，台账随会话保留，实时组身份由服务端迁移；三条口径（仅进行中、满员拒绝、离线放行）均有测试或界面证据。未做项与边界同需求档。

遗留（不阻塞本批）：根 README 中的启动路径仍写 `newproject`（副本未适配的既有问题，未在本批改动）。
