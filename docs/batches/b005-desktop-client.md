# 2026-10-22 · desktop-client

> 六段 append-only，一段一作者：§1 讨论（主 agent）· §2 批次任务与设计（主 agent）· §3 设计评审（评审子代理）· §4 用户批准（主 agent）· §5 实施记录（实施者）· §6 验证与收口（主 agent）。
> 编制：主 agent · 2026-10-22 · 来源 = 用户需求：加客服桌面客户端（Electron 打包，托盘与桌面通知）
> 台账 = #4（customer-chat · 归批）。前情 = b004-stats-dashboard（数据统计）。
> 批档写入通道说明：本会话锚点在用户主目录，批档基址指向 `newproject`，批档工具拒绝跨仓创建与写入；本批六段由主 agent 直接写入本仓，§5 依据实施者报告与主 agent 复核代录。

## §1 讨论（主 agent）

**状态行**：已收口 2026-10-22

本批条目：客服桌面客户端——把现有客服端打包成 macOS 桌面应用，带托盘、系统通知，可配置后端地址。

关键判据：

- 本机没有 Rust 工具链（`~/.cargo` 不存在），Tauri 需要先装 Rust；Electron 只依赖 npm（已确认 registry 可下载 electron 45 / electron-builder 26）。
- 前端已支持构建期 `VITE_API_BASE_URL`（`web/src/lib/http.js:6`、`web/src/lib/signalr.js:4`），桌面端需要把后端地址改成运行时可配置。
- 桌面端加载 `web/dist` 时不能用 `file://`（history 路由会 404），由主进程起本地静态服务器托管 dist。
- 系统通知走主进程 `Notification`（点击可聚焦窗口），渲染进程经 preload 暴露的桥接调用；浏览器环境继续用 Element Plus 通知。

授权口径：用户 2026-10-22 回「可以」批准三批方案，并选择「独立桌面客户端（Electron/Tauri 打包，桌面通知、托盘）」形态。

## §2 批次任务与设计（主 agent）

**状态行**：设计完成

### 任务

`desktop/` 目录产出可运行的 Electron 客服客户端：窗口加载打包后的客服端页面，托盘常驻（显示/隐藏/退出），后端地址可配置（默认 `http://localhost:5180`），新消息/被转派/账号停用时弹 macOS 系统通知，点击通知聚焦窗口；`npm run dist` 产出 macOS 应用。

### 技术方案

- Electron 主进程：单实例锁、窗口状态（简单记忆尺寸）、托盘菜单、通知 IPC、静态服务器。
- 渲染进程：`web/dist` 由主进程内置静态服务器（127.0.0.1）托管；优先用固定端口 47821，被占用时退回随机端口（localStorage 按 origin 隔离，随机端口那一次会丢登录态）；页面通过 `?apiBase=` 或本地配置拿到后端地址。
- 后端地址：`app.getPath('userData')/config.json`（主进程读写）；首次启动默认 `http://localhost:5180`，托盘菜单「设置后端地址」弹输入框后写入并重载窗口。
- 通知桥：preload 用 contextBridge 暴露 `window.desktop.notify({title, body})` 与 `window.desktop.getApiBase()`；网页在存在桥时用桥发通知（点击聚焦窗口），否则退回 Element Plus 通知。
- Web 端改动：`http.js` / `signalr.js` 的基址读取顺序改为「URL 参数 apiBase → localStorage → 构建期变量 → 同源」；新增 `lib/desktop.js` 封装桥调用与通知。
- 打包：`electron-builder --mac --dir`（免签名，产出 .app）；另给 `npm start` 直接跑。

### 受影响文件

新建 `desktop/`：`package.json`、`main.js`、`preload.js`、`server.js`、`README.md`。
前端：`web/src/lib/http.js`、`web/src/lib/signalr.js`、`web/src/lib/desktop.js`（新）、`web/src/views/AgentConsole.vue`（通知接入）。

### 验收对照

- `cd desktop && npm install` 成功；`npm start` 能打开窗口并加载客服端登录页（后端 5180 在跑时）。
- 托盘图标存在，菜单可显示/隐藏/退出。
- 新消息到达时弹系统通知（macOS 通知中心可见），点击通知窗口置顶。
- 修改后端地址后重载，接口请求指向新地址。
- `npm run dist` 产出 `desktop/dist/mac*/ChatDesk 客服.app`。
- `cd web && npm run build` 仍成功。

### 关键决策

- 选 Electron 不选 Tauri：本机无 Rust 工具链，Tauri 需先装 Rust + 系统依赖；Electron 仅 npm 依赖，代价是体积。
- 内置静态服务器而非 `file://`：Vue Router history 模式直接 `file://` 会 404，静态服务器同时解决跨域与路由回退。
- 通知走主进程：渲染进程的 Notification 无法把点击事件聚焦窗口，桥接才能做到。

### 调查证据

已核对前端基址读取、路由模式、现有通知用法（Element Plus `ElNotification`）；npm registry 已确认 electron / electron-builder 可下载。

## §3 设计评审（评审子代理）

（本批为桌面壳层，与 b003/b004 共用窄范围评审；结论回填在 b003 §3。）

## §4 用户批准（主 agent）

**状态行**：已批准

- 2026-10-22：用户选择「独立桌面客户端（Electron/Tauri 打包，桌面通知、托盘）」，属于三批方案中的客户端批次。

## §5 实施记录（实施者）

来源说明：本段由主 agent 依 coder 实施产物与主 agent 实测代录。

### 落地范围

新建 `desktop/`：`package.json`（electron 45 / electron-builder 26）、`main.js`（单实例、窗口、托盘、通知 IPC、静态服务器启动）、`preload.js`（contextBridge：notify / getApiBase）、`server.js`（web/dist 静态服务 + SPA 回退）、`README.md`。
前端：`lib/http.js` / `lib/signalr.js`（基址顺序：apiBase 参数 → localStorage → 构建期变量 → 同源）、`lib/desktop.js`（新）、`AgentConsole.vue`（桌面通知接入）。

### 关键实现

- 主进程读取 `userData/config.json` 的 `apiBase`（缺省 `http://localhost:5180`），加载 `http://127.0.0.1:<端口>/?apiBase=...`。
- 托盘：显示/隐藏、设置后端地址、退出；关闭窗口隐藏到托盘。
- 端口策略：固定优先端口 47821（同 origin 保住 localStorage 登录态），被占用时退回随机端口（那一次需重新登录）。

### 与设计点的偏差

- 无。

## §6 验证与收口（主 agent）

### 验证读数

- `cd desktop && npm install` 成功（node_modules 360 MB）；`node --check` 三个主进程文件均通过。
- `cd web && npm run build` 成功；`electron-builder --mac --dir` 产出 `desktop/dist/mac-arm64/ChatDesk 客服.app`（254 MB）。
- 启动实测：应用进程存活，窗口加载 `http://127.0.0.1:47821/...?apiBase=http://localhost:5180`，登录 agent1 后进入客服工作台（会话列表与输入区正常），截图 `tests/screenshots/07-desktop-agent.png`；截图通过应用内 CDP 捕获（系统 `screencapture` 无录屏权限）。

### 收口结论

R10 桌面客户端落地：可安装的 .app、托盘、后端地址可配、桌面通知路径已实现（通知横幅未做逐条截图）。应用未签名，首次打开需在「系统设置 → 隐私与安全」放行。
