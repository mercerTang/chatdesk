# ChatDesk 客服桌面客户端（Electron）

把 `web/` 构建出的客服端页面打包成 macOS 桌面应用：窗口 + 托盘常驻 + 系统通知，后端地址在客户端内配置。

## 准备

```bash
cd web && npm install && npm run build    # 产出 web/dist，桌面端托管的就是它
cd ../desktop && npm install
```

## 运行

```bash
npm start                 # 开发运行（直接加载 ../web/dist）
npm run dist              # 打包成 .app（未签名），产出在 desktop/dist/mac*/ChatDesk 客服.app
```

后端 `http://localhost:5180` 需要先在跑（仓库根目录 `start.command`）。没启动后端时窗口能打开，但登录会报连接失败。

## 后端地址

- 默认 `http://localhost:5180`，存在 `~/Library/Application Support/chatdesk-desktop/config.json`（`apiBase` 字段）。
- 改地址：托盘菜单 →「设置后端地址…」，保存后窗口自动重新加载。
- 主进程加载页面时带 `?apiBase=<地址>`；网页按 `URL 参数 → localStorage → 构建期 VITE_API_BASE_URL → 同源` 的顺序取基址（`web/src/lib/desktop.js` 的 `resolveApiBase`）。

## 窗口与托盘

- 关窗不退出：窗口收进托盘，托盘菜单可「显示 / 隐藏 / 设置后端地址 / 重新加载 / 退出」。
- 窗口尺寸与位置记在 `config.json` 的 `window` 字段。

## 系统通知

渲染进程通过 preload 暴露的 `window.desktop.notify({ title, body })` 调用主进程 `Notification`，点通知聚焦窗口。
新消息、会话被转派、转入会话、账号停用四个时机触发；浏览器环境没有 `window.desktop`，继续用 Element Plus 通知。

没看到横幅或没声音时，到「系统设置 → 通知 → ChatDesk 客服」确认通知已允许（专注模式也会拦）。

## 文件

| 文件 | 作用 |
| --- | --- |
| `main.js` | 主进程：窗口、托盘、通知 IPC、配置读写、设置弹窗 |
| `server.js` | 内置静态服务器（127.0.0.1，优先端口 47821），history 路由回退 `index.html` |
| `preload.js` | 渲染进程桥：`getApiBase()` / `notify()` |
| `assets/trayTemplate.png` | 菜单栏模板图（黑色 + alpha，自动适配明暗） |
| `build/icon.png` | 应用图标（品牌蓝 `#007cff`） |

## 已知边界

- 未签名：首次打开需要在「系统设置 → 隐私与安全性」里允许；分发要另配开发者证书与公证。
- 优先端口 47821 被别的程序占用时退回随机端口，那一次启动的登录态不保留（localStorage 按 origin 隔离）。
- 只打 macOS 包（`--mac --dir`）；Windows / Linux 需要另加 target。
