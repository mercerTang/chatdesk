# 全量测试报告 · 2026-10-22

> 对象：`chatdesk-copy`（在线客服对话系统）
> 覆盖批次：b001 基础系统 · b002 工单转派 · b003 管理中台 · b004 数据统计 · b005 客服桌面客户端
> 本报告与截图均为交付产物，放在 `tests/` 下；原始输出见同目录日志文件。

## 一、结论

- 后端构建：`dotnet build` — **0 错误、0 警告**。
- 后端测试：`dotnet test` — **57 / 57 通过，失败 0，跳过 0**（历史基线 31 → 本轮扩到 57）。
- 前端构建：`npm run build` — **成功**，产物 `web/dist`（仅既有的 chunk 体积告警）。
- 桌面客户端：`electron-builder --mac --dir` — **产物 `desktop/dist/mac-arm64/ChatDesk 客服.app`（254 MB）**，启动后进入客服工作台，界面见 `screenshots/07-desktop-agent.png`。
- 界面闭环：管理员后台四个页面、客服端转派、管理员强制转派均实测通过（截图见下）。

## 二、环境

| 项 | 值 |
|---|---|
| 时间 | 2026-10-22 15:22（Asia/Shanghai） |
| .NET SDK | 8.0.421（`~/Desktop/开发/dotnet-sdk/dotnet`，不在 PATH） |
| Node / npm | v25.6.1 / 11.9.0 |
| 数据库 | SQLite `ChatServer/chat.db`（WAL）；测试用独立临时库 |
| 后端 / 前端 | http://localhost:5180 / 5173（访客）/ 5174（客服与管理后台） |

## 三、命令与原始输出

```bash
cd ~/Desktop/开发/chatdesk-copy
~/Desktop/开发/dotnet-sdk/dotnet build     # 0 warning / 0 error
~/Desktop/开发/dotnet-sdk/dotnet test      # 通过: 57，失败: 0，总计: 57
cd web && npm run build                                # ✓ built in 2.08s
cd desktop && CSC_IDENTITY_AUTO_DISCOVERY=false npm run dist   # 产出 dist/mac-arm64/ChatDesk 客服.app
```

原始日志：

- `test-run-20261022.log` — build + test 完整输出
- `test-cases-20261022.txt` — 57 个用例的完整名单
- `web-build-20261022.log` — 前端构建输出

## 四、用例清单（57 个，按文件分组）

| 文件 | 数量 | 覆盖点 |
|---|---|---|
| `AdminApiTests.cs` | 7 | 管理端鉴权（401/403）、新增客服与非法输入、改资料/重置密码/停用拦登录、跨客服读会话与工单、强制转派与操作人台账、停用推送 SessionRevoked 且不硬断、停用后不再被分配会话与不可重连 |
| `FileApiTests.cs` | 5 | 上传鉴权 401、正常上传与下载字节一致、非法扩展名 400、超 10 MB 413、图片内容与扩展名不符 400 |
| `AdminStatsTests.cs` | 5 | 按日计数含 0 值日、首响均值与样本数、按客服维度、非 admin 403、days 越界 400 |
| `AuthApiTests.cs` | 8 | 注册登录、重复用户名 409、错密码 401、种子客服可用、无令牌 401、展示名校验、角色越权 403、Hub negotiate 鉴权 |
| `ChatHubTests.cs` | 4 | 发送回执与广播、重复 ClientMessageId 只落一行、已读通知、确认方不收自己的回执 |
| `ConversationApiTests.cs` | 8 | 创建/复用、自动分配、满员 409、越权 403、关闭、mine 过滤、换客服限制、客服端按访客去重 |
| `MessageReliabilityTests.cs` | 5 | 20 并发序号严格递增、幂等重发、afterSeq 增量、关闭后拒绝、内容裁剪与空内容 |
| `NetworkResilienceTests.cs` | 2 | 客服离线消息补拉、断线重连后按水位补齐 |
| `RetentionServiceTests.cs` | 1 | 30 天清理保留工单 |
| `TicketApiTests.cs` | 3 | 工单随会话创建/关闭、详情正序与导出、按客服隔离 |
| `TicketTransferTests.cs` | 9 | 归属迁移与台账、非本人 404、访客 403、转自己 400、已结束 409、目标不存在 404、满员 409、台账正序、实时组迁移且原客服断流 |

完整名单：`test-cases-20261022.txt`。

## 五、界面验证与截图

> 截图仅保存在本地 `tests/screenshots/`，已加入 `.gitignore`，不入库；下表列出本地文件名与验证点。

| 截图 | 内容 | 验证点 |
|---|---|---|
| `screenshots/01-admin-dashboard.png` | 管理后台仪表盘 | 6 个指标卡 + 近 14 天三组柱状图 + 客服排行，数据来自 `GET /api/admin/stats` |
| `screenshots/02-admin-agents.png` | 客服管理 | 3 个客服的在在线状态、负载、启用状态、编辑与重置密码入口 |
| `screenshots/03-admin-conversations.png` | 会话总览 | 跨客服全部会话、筛选（状态/客服/日期） |
| `screenshots/04-admin-ticket-detail.png` | 工单详情抽屉 | 任意工单可读、转派台账、聊天记录、强制转派入口 |
| `screenshots/05-admin-force-transfer.png` | 管理员强制转派 | 目标客服选择 + 备注，说明文字注明「原客服立即失去接收与回复权限」 |
| `screenshots/06-agent-transfer-dialog.png` | 客服端转派（b002） | 工单记录页「转派」按钮与对话框，目标客服带在线/负载标注 |
| `screenshots/07-desktop-agent.png` | Electron 桌面客户端 | 客服工作台在桌面端运行，顶部「已连接」，会话列表与输入区正常 |
| `screenshots/08-rich-text.png` | 富文本消息 | 工具栏 + 气泡内的粗体、行内代码、引用、列表与表情渲染 |
| `screenshots/09-editor-upload.png` | 所见即所得与上传 | 输入框内即时格式、粘贴上传的图片内联显示、附件以文件名链接出现 |
| `screenshots/10-link-contrast.png` | 链接对比度修正 | 自发消息蓝色气泡内的附件链接改为白色下划线 |
| `screenshots/11-composer-layout.png` | 输入区布局修正 | 最后一条消息的时间与回执完整显示在输入区上方 |
| `screenshots/12-toolbar-icons.png` | 代码按钮图标 | 行内代码 `</>` 与代码块 `{ }` 两个按钮外观可区分 |
| `screenshots/13-toolbar-tooltip.png` | 工具栏悬停提示 | 悬停「行内代码」时按钮下方浮出「行内代码：选中一段文字」 |

## 六、接口冒烟（手动）

```bash
# 管理员登录
POST /api/auth/login {"username":"admin","password":"Admin@123456"}   → 200 + role=admin
# 跨客服工单
GET  /api/admin/tickets            → 4 条（Mia / Leo / Iris 名下都在）
# 统计
GET  /api/admin/stats?days=7       → overview{conversationCount:4, messageCount:5, ticketCount:4, transferCount:2, onlineAgentCount:1, openTicketCount:1}
                                     daily 含 0 值日；agents 维度按客服返回
# 转派（客服端，b002）
POST /api/tickets/{id}/transfer    → 200；原客服列表消失、目标客服可见、访客端换名
```

## 七、未覆盖与说明

- 桌面客户端的系统通知（macOS 通知中心）未做逐条截图：需要在真实桌面会话里观察通知横幅；代码路径已实现（主进程 `Notification` + 点击聚焦），未做自动化断言。
- 管理后台的「停用客服」前端登出提示依赖 `SessionRevoked` 事件，已有后端测试断言事件发出，前端提示未单独截图。
- 截图来自隔离端口（后端 5182 + 前端 5175 + 临时库）与正式端口两轮实测；正式端口的服务由 `start.command` 启动。
- 浏览器截图通过 Electron/Chromium 的页面捕获完成；macOS `screencapture` 因系统未授予屏幕录制权限不可用，桌面端截图改用应用内 CDP 捕获。

## 八、评审后修复

独立代码评审（范围：AdminController / ChatService / ConversationRealtimeService / DatabaseInitializer）结论为 changes-required，发现一条必须修：停用只在登录入口生效，自动分配、访客显式指定与转派目标仍会选中已停用客服，且停用前的 token 仍可重连 Hub。已修：

- 自动分配排除 `IsActive = false`；访客显式指定与转派目标按「不存在」拒绝。
- `ChatHub.OnConnectedAsync` 拒绝停用账号建连并 Abort；`RequireAccountAsync` 对停用客服抛 403。
- 新增用例 `DeactivatedAgent_IsExcludedFromAssignment_AndCannotReconnect` 锁住这条口径。
- 其余 4 条非必修（种子管理员同名冲突告警、统计窗口与保留期口径、无分页、种子密码硬编码）已分别修复或记录取舍。

## 九、富文本批次（b006）

- 前端构建：`cd web && npm run build` 成功（15:38）。
- 界面：客服工作台发送含粗体、行内代码、引用、列表与表情的消息，气泡按格式渲染（`screenshots/08-rich-text.png`）。
- 安全：发送 `<img src=x onerror="window.__xss=1"> <script>window.__xss2=1</script> **安全测试**`，气泡内按文本显示，`window.__xss`/`window.__xss2` 均为 null，粗体仍生效。
- 后端无改动，52/52 用例不受影响；桌面客户端已用新构建重新打包（15:39）。

## 十、所见即所得与上传批次（b007）

- 后端：`dotnet test` 57/57（新增 5 个文件上传用例：鉴权、扩展名白名单、10 MB 上限、图片文件头、下载字节一致）。
- 前端：`npm run build` 成功；输入框为 Quill 所见即所得编辑器，加粗在输入框内即时生效。
- 上传实测：粘贴图片 → 上传返回 `/api/files/2` 并在气泡内联显示；附件上传 → `说明.txt` 以链接出现。
- 下载头：图片 `Content-Type: image/png` 内联；附件 `Content-Disposition: attachment; filename*=UTF-8''说明.txt`。
- 截图：`screenshots/09-editor-upload.png`。
- 桌面客户端已用新构建重新打包。
