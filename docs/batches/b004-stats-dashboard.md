# 2026-10-22 · stats-dashboard

> 六段 append-only，一段一作者：§1 讨论（主 agent）· §2 批次任务与设计（主 agent）· §3 设计评审（评审子代理）· §4 用户批准（主 agent）· §5 实施记录（实施者）· §6 验证与收口（主 agent）。
> 编制：主 agent · 2026-10-22 · 来源 = 用户需求：加客服桌面客户端、中台与数据统计（本批为数据统计）
> 台账 = #3（customer-chat · 归批）。前情 = b003-admin-console（管理中台）。
> 批档写入通道说明：本会话锚点在用户主目录，批档基址指向 `newproject`，批档工具拒绝跨仓创建与写入；本批六段由主 agent 直接写入本仓，§5 依据实施者报告与主 agent 复核代录。

## §1 讨论（主 agent）

**状态行**：已收口 2026-10-22

本批条目：数据统计看板——管理员在仪表盘看到概览指标、近 N 天按日趋势与按客服维度的统计。

关键判据：

- 现有数据都在 `Conversations` / `Messages` / `Tickets` / `TicketTransfers` 四张表（见设计档数据模型），没有统计表，全部按需聚合。
- 工单与会话的 30 天保留策略（设计档「数据保留」）只删消息，会话与工单保留，因此按日统计以会话创建时间与消息发送时间为准。
- 首响口径：会话内「首条访客消息 → 首条客服回复」的时长；无客服回复的会话不计入均值（样本数单独给出）。
- 仪表盘页在 b003 已落地指标卡与最近工单，本批替换为真实统计接口 + 按日柱状图 + 客服排行。

授权口径：用户 2026-10-22 回「可以」，批准三批方案；本批是其中的统计批次。

## §2 批次任务与设计（主 agent）

**状态行**：设计完成

### 任务

管理员在 `/admin` 仪表盘看到：概览指标（会话、消息、工单、转派、在线客服）、近 14 天按日趋势（新会话 / 消息 / 工单）、按客服的接待量与首响统计。

### 接口

`GET /api/admin/stats?days=14`（admin 角色），返回：

- `overview`：`conversationCount` / `messageCount` / `ticketCount` / `transferCount` / `onlineAgentCount` / `openTicketCount` / `avgFirstResponseSeconds`（无样本为 null）/ `firstResponseSamples`
- `daily`：`[{ date, conversations, messages, tickets }]`，按天升序，包含 0 值日
- `agents`：`[{ agentId, displayName, conversationCount, messageCount, avgFirstResponseSeconds, firstResponseSamples, activeConversationCount }]`

口径：
- 时间窗口：`[今天-days+1, 明天)`，按服务端本地日期切分；`days` 限 1-90，默认 14。
- 会话数与工单数按 `CreatedAt` 落窗；消息数按 `SentAt` 落窗。
- 首响按会话计算，只统计窗口内创建的会话；同会话内取首条 `visitor` 消息与首条晚于它的 `agent` 消息。
- 在线客服数取 `AgentPresenceService`（进程内实时值），不计入窗口。

### 前端

`AdminDashboard.vue`：概览指标卡 + 近 14 天柱状图（纯 CSS/SVG，不引图表库）+ 按客服排行表；数据来自 `api.adminStats(days)`。

### 受影响文件

后端：`Dtos/AdminDtos.cs`、`Controllers/AdminController.cs`（新增 stats 端点）。
前端：`web/src/lib/api.js`、`web/src/views/admin/AdminDashboard.vue`。
测试：`tests/ChatServer.Tests/AdminStatsTests.cs`（新）。

### 验收对照

- 造两张不同天的会话/消息后，`daily` 对应日期计数正确，0 值日存在。
- 首响：访客先发、客服后回，均值等于两者差；无回复会话不改变均值但 `firstResponseSamples` 不增。
- 按客服维度：各自会话数、消息数、活跃数正确。
- 非 admin 调用 403；`days` 越界 400。
- `dotnet build` 0 错误、`dotnet test` 全绿、`npm run build` 通过。

### 关键决策

- 最接近的先例：管理端列表接口（b003）。对齐：admin 鉴权、DTO 形状、投影方式；偏离：统计需要跨表聚合，故单独端点与 DTO。
- 先在服务端按窗口取最小列集再在内存聚合：SQLite 上比相关子查询可读，演示规模足够；数据量上来再改 SQL 聚合，属扩展点。
- 图表用 CSS 柱状：不引入 ECharts（几百 KB），指标简单，样式与现有品牌色一致。

### 调查证据

已核对数据模型、保留策略、管理端鉴权与前端骨架（坐标见设计档）；不引入新依赖。

## §3 设计评审（评审子代理）

（本批与 b003 共用一次窄范围评审，结论回填在 b003 §3。）

## §4 用户批准（主 agent）

**状态行**：已批准

- 2026-10-22：统计批次属于用户批准的「中台 → 统计 → 桌面客户端」三批方案。

## §5 实施记录（实施者）

来源说明：本段由主 agent 依 coder 实施报告与复核代录。

### 落地范围

后端：`Dtos/AdminDtos.cs`（AdminStatsDto / Overview / Daily / AgentStat）、`Controllers/AdminController.cs`（`GET stats`）。
前端：`lib/api.js`（adminStats）、`views/admin/AdminDashboard.vue`（重写：指标卡 + 三组 CSS 柱状图 + 客服排行）。
测试：`tests/ChatServer.Tests/AdminStatsTests.cs`（5 例）。

### 关键实现

- 窗口 `[今天-days+1, 明天)`，`days` 限 1-90；按服务端本地日期切分，daily 补 0 值日。
- 首响：会话内首条 `visitor` 消息 → 首条晚于它的 `agent` 消息；无回复不计入均值，样本数单独返回。
- 按客服维度：名下会话数、消息数、平均首响、当前活跃会话。
- 图表纯 div/CSS，无新增依赖。

### 与设计点的偏差

无。

## §6 验证与收口（主 agent）

### 验证读数

- `dotnet build` 0 错误；`dotnet test` 52/52（含本批 5 例）。
- `GET /api/admin/stats?days=7` 实测：overview{会话 4、消息 5、工单 4、转派 2、在线客服 1、进行中工单 1}；daily 含 0 值日；agents 按客服返回。
- 看板界面实测：6 张指标卡 + 近 14 天趋势 + 客服排行，截图 `tests/screenshots/01-admin-dashboard.png`。

### 收口结论

R9 数据统计落地：概览、按日趋势（含 0 值日）、按客服维度、首响口径与样本数都有接口与测试；图表不引入新依赖。
