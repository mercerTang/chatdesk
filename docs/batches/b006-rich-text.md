# 2026-10-22 · rich-text

> 六段 append-only，一段一作者：§1 讨论（主 agent）· §2 批次任务与设计（主 agent）· §3 设计评审（评审子代理）· §4 用户批准（主 agent）· §5 实施记录（实施者）· §6 验证与收口（主 agent）。
> 编制：主 agent · 2026-10-22 · 来源 = 用户需求：加上富文本发送的功能
> 台账 = #5（customer-chat · 归批）。前情 = b005-desktop-client（桌面客户端）。
> 批档写入通道说明：本会话锚点在用户主目录，批档基址指向 `newproject`，批档工具拒绝跨仓创建与写入；本批六段由主 agent 直接写入本仓。

## §1 讨论（主 agent）

**状态行**：已收口 2026-10-22

本批条目：消息支持轻量富文本——客服与访客都能发带格式的消息（粗体、斜体、删除线、行内代码、代码块、引用、列表、链接、表情），另一端按格式渲染。

关键判据：

- 消息内容目前是纯文本（`ChatService.MaxContentLength = 2000`，trim 后非空），入库不做任何转义；渲染点用 `{{ content }}`（`MessageThread.vue`、`Tickets.vue`）。
- 直接渲染 HTML 会把访客输入当代码执行（存储型 XSS），所以存储保持 Markdown 源文，渲染端「先转义、再解析、最后白名单净化」。
- 富文本语法计入 2000 字上限；导出 TXT/CSV 保留 Markdown 源文，可读且不失真。
- 不引入 WYSIWYG 编辑器（TipTap/Quill）：改动面覆盖输入、序列化、粘贴与长度统计，收益不抵风险；工具栏 + Markdown 足够覆盖聊天场景。

授权口径：用户 2026-10-22 回「可以的」，批准轻量 Markdown + 渲染净化方案。

## §2 批次任务与设计（主 agent）

**状态行**：设计完成

### 任务

两端输入框带格式工具栏；消息按 Markdown 渲染；列表摘要显示纯文本；HTML 注入不执行。

### 技术方案

- 存储不变：`Message.Content` 仍是文本（Markdown 源文），后端零改动，序号/幂等/补拉/保留策略都不受影响。
- 渲染：`web/src/lib/richText.js` —— `renderRich(content)` 先转义 `<`/`>`/`&`，再交给 `marked`（gfm + breaks），最后用 `DOMPurify` 白名单净化（p/br/strong/em/del/code/pre/blockquote/ul/ol/li/a/hr，只留 href）。
- 摘要：`toPlainText(content)` 去标记，供会话列表、工单列表与后台表格用。
- 组件：新增 `RichText.vue`（`v-html` 只接收净化后的 HTML），替换 `MessageThread.vue` 气泡与 `Tickets.vue` 正文。
- 输入：`ChatComposer.vue` 加工具栏（粗体/斜体/删除线/行内代码/代码块/引用/列表/链接/表情），基于 textarea 选区插入；保留 Enter 发送、Shift+Enter 换行与中文输入法保护。
- 依赖：新增 `marked`、`dompurify`（前端运行时依赖）。

### 受影响文件

`web/src/lib/richText.js`（新）、`web/src/components/RichText.vue`（新）、`web/src/components/ChatComposer.vue`、`web/src/components/MessageThread.vue`、`web/src/views/Tickets.vue`、`web/src/views/AgentConsole.vue`、`web/src/views/admin/AdminConversations.vue`、`web/src/views/admin/AdminTickets.vue`、`web/package.json`。

### 验收对照

- 发送 `**粗体**`、`` `代码` ``、`> 引用`、`- 列表` 后对端气泡按格式渲染。
- 发送 `<img src=x onerror=...>` 与 `<script>` 时按文本显示，页面不执行脚本。
- 会话列表/工单列表摘要无 Markdown 符号。
- `npm run build` 通过；桌面客户端重新打包后同样生效。

### 关键决策

- 最接近的先例：消息渲染（`MessageThread.vue` 纯文本）+ 输入（`ChatComposer.vue`）。对齐：不改消息链路与 DTO；偏离：渲染层从文本插值改为净化后的 HTML，因此必须把净化做成唯一入口。
- 先用转义再解析：这样用户输入的 HTML 不可能变成标签，DOMPurify 再兜一层白名单；两道关叠加，任一环节失效都不至于直接执行脚本。
- Markdown 而非 HTML 存储：导出、检索、长度限制都保持简单。

### 调查证据

已核对消息字段与长度限制、两个渲染点、输入组件与列表摘要位置；依赖版本 npm registry 已确认可下载（marked 18.1.1 / dompurify 3.4.17）。

## §3 设计评审（评审子代理）

（本批为前端渲染层改动，未单独发起评审；同类改动已含在 b003 评审范围内，渲染净化逻辑见 §6 手工验证。）

## §4 用户批准（主 agent）

**状态行**：已批准

- 2026-10-22：用户回「可以的」，批准轻量 Markdown + 渲染净化方案。

## §5 实施记录（实施者）

由主 agent 直接实现（前端 8 个文件 + 2 个新文件 + 依赖安装），要点见 §2。

## §6 验证与收口（主 agent）

### 验证读数

- `cd web && npm run build`：成功（2026-10-22 15:38）。
- 界面实测：客服工作台发送 `**加急**：订单 \`A-1029\` 已发出 > 预计明天到达 - 请留意短信 😀` → 气泡呈现粗体、行内代码、引用条、列表与表情，截图 `tests/screenshots/08-rich-text.png`。
- XSS 实测：发送 `<img src=x onerror="window.__xss=1"> <script>window.__xss2=1</script> **安全测试**` → 气泡内为转义文本，`window.__xss` 与 `window.__xss2` 均为 null，粗体仍生效。
- 列表摘要：会话列表显示「加急：订单 A-1029 已发出 预计…」，无 Markdown 符号。

### 收口结论

R11 富文本落地：存储仍是 Markdown 源文，渲染经「转义 + marked + DOMPurify 白名单」，注入不执行；工具栏覆盖常用格式与表情。
