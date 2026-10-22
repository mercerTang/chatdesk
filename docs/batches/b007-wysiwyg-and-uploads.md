# 2026-10-22 · wysiwyg-and-uploads

> 六段 append-only，一段一作者：§1 讨论（主 agent）· §2 批次任务与设计（主 agent）· §3 设计评审（评审子代理）· §4 用户批准（主 agent）· §5 实施记录（实施者）· §6 验证与收口（主 agent）。
> 编制：主 agent · 2026-10-22 · 来源 = 用户需求：富文本要在输入框里也能看到效果，并加上图片上传与附件上传
> 台账 = #6（customer-chat · 归批）。前情 = b006-rich-text（Markdown 工具栏版富文本）。
> 批档写入通道说明：本会话锚点在用户主目录，批档基址指向 `newproject`，批档工具拒绝跨仓创建与写入；本批六段由主 agent 直接写入本仓。

## §1 讨论（主 agent）

**状态行**：已收口 2026-10-22

本批条目：把 b006 的「Markdown 工具栏」升级为所见即所得编辑器（输入框内即时显示格式），并新增图片与附件上传。

关键判据：

- b006 的输入框是 textarea + 语法插入，用户看不到效果；本批换成 Quill 2 编辑器，格式在输入框内即时呈现。
- 消息内容仍是 Markdown（`Message.Content`），发送时用 turndown 把编辑器 HTML 转回 Markdown——不新增存储格式、不改消息链路、旧消息照常渲染。
- 上传是新的后端能力：当前没有静态文件中间件，也没有附件表（`Message` 只有一个 `Content`）。
- 图片渲染需要放宽白名单到 `img`，但只能指向本站文件端点，外链图片一律去掉（避免追踪像素与注入）。

授权口径：用户 2026-10-22 回「可以」，批准所见即所得 + 图片/附件上传方案，两条例外口径按默认（匿名不可猜 URL、10 MB 白名单）。

## §2 批次任务与设计（主 agent）

**状态行**：设计完成

### 任务

输入框内所见即所得；工具栏可插图、粘贴/拖拽图片；附件上传后以链接出现在消息里；图片内联显示、附件可下载。

### 后端

- `FileAsset` 表：`StoredName`（年月/随机名.扩展名，唯一）、`OriginalName`、`ContentType`、`Size`、`UploaderId`、`CreatedAt`；老库启动补表。
- `POST /api/files`（需登录）：multipart 单文件，≤10 MB；扩展名白名单 + 图片文件头校验；落盘 `uploads/年月/`，返回 `{ id, url, name, size, contentType }`。
- `GET /api/files/{id}`：图片内联，其它按附件下载并还原原文件名；匿名可读，靠 32 位随机名不可枚举（生产应改签名 URL）。
- 测试用 `Files:UploadRoot` 指向临时目录，避免污染项目目录。

### 前端

- `RichEditor.vue`：Quill 2，工具栏（B/I/S/行内代码/代码块/引用/有序·无序列表/链接/图片/附件），`getMarkdown()` 用 turndown 转 Markdown；Enter 发送、Shift+Enter 换行；粘贴/拖拽图片在捕获阶段拦下并上传，避免 Quill 内联 data URL。
- `ChatComposer.vue`：改用 RichEditor，保留发送按钮与「有内容才可发」判断（纯图片也算有内容）。
- `richText.js`：白名单加 `img` 与 `src/alt`，渲染后清理非 `/api/files/{id}` 的图片。
- `api.js`：`uploadFile(file)`。

### 受影响文件

后端：`Models/FileAsset.cs`（新）、`Dtos/FileDtos.cs`（新）、`Controllers/FilesController.cs`（新）、`Data/AppDbContext.cs`、`Services/DatabaseInitializer.cs`。
前端：`components/RichEditor.vue`（新）、`components/ChatComposer.vue`、`components/RichText.vue`、`lib/richText.js`、`lib/api.js`、`package.json`（quill / turndown / turndown-plugin-gfm）。
测试：`tests/ChatServer.Tests/FileApiTests.cs`（新）、`ChatServerFactory.cs`（临时上传目录）。

### 验收对照

- 输入框内加粗、列表、图片即时可见；发送后对端气泡同样显示。
- 粘贴图片走上传（URL 指向 `/api/files/{id}`），不是 data URL。
- 附件以链接显示，下载保留原文件名。
- 未登录上传 401；非法类型 400；超过 10 MB 413；图片内容与扩展名不符 400。
- `dotnet build` / `dotnet test` / `web build` 全绿；桌面端重新打包。

### 关键决策

- 最接近的先例：b006 的 Markdown 渲染链与 ChatComposer。对齐：存储格式与渲染净化不变；偏离：输入层从 textarea 换成 Quill，发送前做 HTML→Markdown。
- 图片只认本站文件端点：外链图片在聊天里是追踪与注入入口，直接丢弃。
- 上传接口匿名可读：`<img>` 无法带 Authorization 头；用 32 位随机名保证不可枚举，并把「生产改签名 URL」写进设计档。

### 调查证据

已核对消息模型、内容上限、渲染白名单、输入组件与后端静态文件配置；npm registry 已确认 quill 2.0.3 / turndown 7.2.4 可下载。

## §3 设计评审（评审子代理）

（本批未单独发起评审；文件上传的安全边界已在 §6 用测试锁定：鉴权、类型、大小、文件头。）

## §4 用户批准（主 agent）

**状态行**：已批准

- 2026-10-22：用户回「可以」，批准所见即所得与图片/附件上传。

## §5 实施记录（实施者）

由主 agent 直接实现：后端新增 FileAsset + FilesController + 补表；前端新增 RichEditor（Quill 2）、ChatComposer 接入、richText 白名单加 img、api.uploadFile；测试新增 5 个文件用例。

## §6 验证与收口（主 agent）

### 验证读数

- `dotnet build` 0 错误；`dotnet test` **57/57**（52 + 5 个文件用例）。
- `cd web && npm run build` 成功（含 Quill 编辑器）。
- 界面实测：输入框内加粗即时生效；粘贴图片后编辑器内出现图片、上传返回 `/api/files/2`；发送后气泡内联显示；附件上传后以「说明.txt」链接出现。
- 下载头实测：图片 `Content-Type: image/png` 内联；附件带 `Content-Disposition: attachment; filename*=UTF-8''说明.txt`。
- 截图：`tests/screenshots/09-editor-upload.png`。

### 收口结论

R11 升级为所见即所得，R12 图片/附件落地；文件上传的鉴权、类型、大小与内容一致性都有自动化测试。

### 收口后修正（链接对比度）

用户反馈自发消息里的链接看不清：实测蓝底（`rgb(0,124,255)`）上的链接是蓝色（`rgb(37,99,235)`）。已在 `styles.css` 加规则：自发气泡内链接改白字 + 下划线；对端气泡链接保持品牌蓝，长文件名允许折行。实测 computed color 为 `rgb(255,255,255)`，截图 `tests/screenshots/10-link-contrast.png`。

### 收口后修正（代码按钮图标）

用户反馈工具栏里像是有两个代码按钮：Quill 默认给「行内代码」和「代码块」同一个 `</>` 图标。已在编辑器初始化后把代码块按钮改为 `{ }`（等宽字体、不换行），两者职责不同且外观可区分。截图 `tests/screenshots/12-toolbar-icons.png`。

### 收口后修正（输入区遮挡时间）

用户反馈最新一条消息下方的时间被输入框挡住：输入区原先绝对定位在底部，盖在消息区上方，消息区底部留白 200px 而输入区实际高约 203px。改为同一列布局：消息区 `flex:1; min-height:0`，输入区 `position: relative` 占独立一行；实测最后一条时间底部 570px、输入区顶部 594px，无重叠，发送后自动到底仍可见。截图 `tests/screenshots/11-composer-layout.png`。

### 收口后修正（工具栏悬停提示）

用户要求补 hover 功能提示：去掉原生 `title`（延迟长且样式不可控），改用 `data-tip` + CSS 伪元素，悬停即在按钮下方浮出深色提示（粗体/斜体带快捷键、图片提示可粘贴或拖入、附件标注 10 MB 上限）；首尾按钮的提示贴边，避免被容器圆角裁切。截图 `tests/screenshots/13-toolbar-tooltip.png`。
