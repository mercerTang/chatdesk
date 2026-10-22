'use strict'

/**
 * ChatDesk 客服桌面客户端主进程。
 *
 * 职责：窗口 + 托盘常驻 + 系统通知 + 内置静态服务器（托管 web/dist）+ 后端地址配置。
 * 渲染进程只通过 preload 暴露的 window.desktop 调用通知与读取地址，页面本身仍是普通的网页应用。
 */

const fs = require('node:fs')
const path = require('node:path')
const {
  app,
  BrowserWindow,
  Menu,
  Notification,
  Tray,
  dialog,
  ipcMain,
  nativeImage,
  shell
} = require('electron')
const { startStaticServer } = require('./server')

const APP_TITLE = 'ChatDesk 客服'
const DEFAULT_API_BASE = 'http://localhost:5180'
// 优先端口固定：页面 origin 稳定，localStorage（登录 token、上次打开的会话）才能跨重启保留
const PREFERRED_PORT = 47821
const WINDOW_DEFAULTS = { width: 1280, height: 820 }

let mainWindow = null
let tray = null
let staticServer = null
let quitting = false
let settingInFlight = false

// ------------------------------------------------------------------ 配置

function configFile() {
  return path.join(app.getPath('userData'), 'config.json')
}

/** 去掉首尾空白与结尾斜杠：拼接 `/api`、`/hubs/chat` 时不能出现双斜杠。 */
function normalizeApiBase(value) {
  return String(value == null ? '' : value)
    .trim()
    .replace(/\/+$/, '')
}

function isValidApiBase(value) {
  if (!value) {
    return false
  }
  try {
    const parsed = new URL(value)
    return parsed.protocol === 'http:' || parsed.protocol === 'https:'
  } catch {
    return false
  }
}

function readConfig() {
  try {
    const parsed = JSON.parse(fs.readFileSync(configFile(), 'utf8'))
    return parsed && typeof parsed === 'object' ? parsed : {}
  } catch {
    // 首次启动没有配置文件，或文件被改坏：都按空配置处理
    return {}
  }
}

function writeConfig(patch) {
  const next = { ...readConfig(), ...patch }
  try {
    fs.mkdirSync(path.dirname(configFile()), { recursive: true })
    fs.writeFileSync(configFile(), `${JSON.stringify(next, null, 2)}\n`, 'utf8')
  } catch (error) {
    console.error('[desktop] 写入配置失败:', error)
  }
  return next
}

function currentApiBase() {
  const configured = normalizeApiBase(readConfig().apiBase)
  return isValidApiBase(configured) ? configured : DEFAULT_API_BASE
}

// ------------------------------------------------------------------ 窗口

function readWindowBounds() {
  const saved = readConfig().window
  const bounds = { width: WINDOW_DEFAULTS.width, height: WINDOW_DEFAULTS.height }
  if (!saved || typeof saved !== 'object') {
    return bounds
  }
  const width = Number(saved.width)
  const height = Number(saved.height)
  if (Number.isFinite(width) && width >= 960) {
    bounds.width = Math.round(width)
  }
  if (Number.isFinite(height) && height >= 600) {
    bounds.height = Math.round(height)
  }
  const x = Number(saved.x)
  const y = Number(saved.y)
  if (Number.isFinite(x) && Number.isFinite(y)) {
    bounds.x = Math.round(x)
    bounds.y = Math.round(y)
  }
  return bounds
}

function saveWindowBounds() {
  if (!mainWindow || mainWindow.isDestroyed() || mainWindow.isMaximized() || mainWindow.isFullScreen()) {
    return
  }
  writeConfig({ window: mainWindow.getBounds() })
}

/** 页面地址带上当前后端地址：?apiBase= 是主进程把配置交给渲染进程的同步通道。 */
function pageUrl() {
  return `${staticServer.url}?apiBase=${encodeURIComponent(currentApiBase())}`
}

function revealWindow() {
  if (!mainWindow || mainWindow.isDestroyed()) {
    createWindow()
    return
  }
  if (mainWindow.isMinimized()) {
    mainWindow.restore()
  }
  mainWindow.show()
  mainWindow.focus()
  app.focus({ steal: true })
}

function hideWindow() {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.hide()
  }
}

function createWindow() {
  mainWindow = new BrowserWindow({
    ...readWindowBounds(),
    minWidth: 960,
    minHeight: 600,
    show: false,
    title: APP_TITLE,
    backgroundColor: '#0f1420',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  })

  mainWindow.once('ready-to-show', () => mainWindow.show())

  // 关窗不退出：客服端要常驻托盘，退出走托盘菜单或 Cmd+Q
  mainWindow.on('close', (event) => {
    saveWindowBounds()
    if (quitting) {
      return
    }
    event.preventDefault()
    mainWindow.hide()
  })

  mainWindow.on('closed', () => {
    mainWindow = null
  })

  // 页面里的外链交给系统浏览器，不在客户端里开新窗口
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('http://') || url.startsWith('https://')) {
      shell.openExternal(url)
    }
    return { action: 'deny' }
  })

  mainWindow.loadURL(pageUrl())
}

function reloadWindow() {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.loadURL(pageUrl())
  }
}

// ------------------------------------------------------------------ 托盘

function trayImage() {
  const image = nativeImage.createFromPath(path.join(__dirname, 'assets', 'trayTemplate.png'))
  // 模板图由 macOS 按菜单栏明暗自动上色
  image.setTemplateImage(true)
  return image
}

function buildTrayMenu() {
  return Menu.buildFromTemplate([
    { label: `显示 ${APP_TITLE}`, click: () => revealWindow() },
    { label: '隐藏窗口', click: () => hideWindow() },
    { type: 'separator' },
    { label: `后端地址：${currentApiBase()}`, enabled: false },
    { label: '设置后端地址…', click: () => promptApiBase() },
    { label: '重新加载页面', click: () => reloadWindow() },
    { type: 'separator' },
    { label: `退出 ${APP_TITLE}`, click: () => app.quit() }
  ])
}

function refreshTray() {
  if (!tray || tray.isDestroyed()) {
    return
  }
  tray.setToolTip(`${APP_TITLE} · ${currentApiBase()}`)
  tray.setContextMenu(buildTrayMenu())
}

function createTray() {
  tray = new Tray(trayImage())
  refreshTray()
  // 点托盘图标直接开窗（右键仍是菜单）
  tray.on('click', () => revealWindow())
}

// ------------------------------------------------------------------ 后端地址设置

function settingsHtml(current) {
  const escaped = current.replace(/[&<>"]/g, (char) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[char])
  return `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<title>设置后端地址</title>
<style>
  :root { color-scheme: light; }
  body {
    margin: 0; padding: 22px 24px 18px;
    font: 13px/1.6 -apple-system, "PingFang SC", "Helvetica Neue", sans-serif;
    color: #2b2f36; background: #ffffff;
  }
  h1 { margin: 0 0 4px; font-size: 14px; font-weight: 600; }
  p { margin: 0 0 14px; color: #6b7280; font-size: 12px; }
  input {
    width: 100%; box-sizing: border-box; padding: 8px 10px;
    border: 1px solid #d4d8e0; border-radius: 6px; font-size: 13px; outline: none;
  }
  input:focus { border-color: #007cff; box-shadow: 0 0 0 3px rgba(0, 124, 255, 0.15); }
  .actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
  button {
    padding: 6px 16px; border-radius: 6px; font-size: 13px; cursor: pointer;
    border: 1px solid #d4d8e0; background: #ffffff; color: #2b2f36;
  }
  button.primary { border-color: #007cff; background: #007cff; color: #ffffff; }
  button:focus-visible { outline: 2px solid #007cff; outline-offset: 1px; }
</style>
</head>
<body>
  <h1>后端地址</h1>
  <p>留空恢复默认 ${DEFAULT_API_BASE}；保存后页面自动重新加载。</p>
  <input id="value" type="text" value="${escaped}" placeholder="${DEFAULT_API_BASE}" spellcheck="false" />
  <div class="actions">
    <button id="cancel" type="button">取消</button>
    <button id="ok" type="button" class="primary">保存</button>
  </div>
</body>
</html>`
}

/**
 * 弹一个输入窗，返回用户填的地址；取消/关窗返回 null。
 * 用 executeJavaScript 取结果，省掉给设置窗再配一个 preload。
 */
async function askApiBase(current) {
  // 窗口收在托盘里时不能挂成 sheet：父窗口不可见，sheet 也跟着看不见
  const parent = mainWindow && !mainWindow.isDestroyed() && mainWindow.isVisible() ? mainWindow : undefined
  const win = new BrowserWindow({
    width: 460,
    height: 226,
    parent,
    modal: Boolean(parent),
    resizable: false,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    title: '设置后端地址',
    show: false,
    webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true }
  })

  try {
    await win.loadURL(`data:text/html;charset=utf-8,${encodeURIComponent(settingsHtml(current))}`)
    win.show()
    return await win.webContents.executeJavaScript(`
      new Promise((resolve) => {
        const input = document.getElementById('value')
        const done = (value) => resolve(value)
        document.getElementById('ok').addEventListener('click', () => done(input.value))
        document.getElementById('cancel').addEventListener('click', () => done(null))
        input.addEventListener('keydown', (event) => {
          if (event.key === 'Enter') done(input.value)
          if (event.key === 'Escape') done(null)
        })
        input.focus()
        input.select()
      })
    `)
  } catch (error) {
    // 关窗会打断这个 Promise，按取消处理
    console.error('[desktop] 设置窗口未返回结果:', error)
    return null
  } finally {
    if (!win.isDestroyed()) {
      win.destroy()
    }
  }
}

async function promptApiBase() {
  if (settingInFlight) {
    return
  }
  settingInFlight = true
  try {
    const current = currentApiBase()
    const entered = await askApiBase(current)
    if (entered == null) {
      return
    }
    const next = normalizeApiBase(entered) || DEFAULT_API_BASE
    if (!isValidApiBase(next)) {
      await dialog.showMessageBox({
        type: 'warning',
        message: '后端地址格式不对',
        detail: `请填 http:// 或 https:// 开头的完整地址，例如 ${DEFAULT_API_BASE}。`,
        buttons: ['知道了']
      })
      return
    }
    writeConfig({ apiBase: next })
    refreshTray()
    reloadWindow()
  } finally {
    settingInFlight = false
  }
}

// ------------------------------------------------------------------ 系统通知

function trustedSender(event) {
  return Boolean(mainWindow && !mainWindow.isDestroyed() && event.sender === mainWindow.webContents)
}

function showNotification(payload) {
  if (!Notification.isSupported()) {
    return false
  }
  const title = String((payload && payload.title) || APP_TITLE).slice(0, 120)
  const body = String((payload && payload.body) || '').slice(0, 400)

  const notification = new Notification({ title, body })
  // 点通知回到客户端：客服在处理别的窗口时不至于漏掉会话
  notification.on('click', () => revealWindow())
  notification.show()
  return true
}

ipcMain.handle('desktop:get-api-base', (event) => (trustedSender(event) ? currentApiBase() : DEFAULT_API_BASE))

ipcMain.handle('desktop:notify', (event, payload) => {
  if (!trustedSender(event)) {
    return false
  }
  return showNotification(payload)
})

// ------------------------------------------------------------------ 启动与退出

function resolveWebRoot() {
  const candidates = [
    // 打包后：electron-builder 把 web/dist 放进 Resources/web
    process.resourcesPath ? path.join(process.resourcesPath, 'web') : null,
    // 开发期（npm start）：desktop/../web/dist
    path.join(__dirname, '..', 'web', 'dist')
  ].filter(Boolean)

  return candidates.find((dir) => fs.existsSync(path.join(dir, 'index.html'))) || null
}

async function boot() {
  const webRoot = resolveWebRoot()
  if (!webRoot) {
    dialog.showErrorBox(
      '缺少前端构建产物',
      '没有找到 web/dist。请先在 web 目录执行 npm install && npm run build，再启动桌面端。'
    )
    app.quit()
    return
  }

  staticServer = await startStaticServer({ root: webRoot, preferredPort: PREFERRED_PORT })
  console.log(`[desktop] 静态服务器 ${staticServer.url} → ${webRoot}`)

  createWindow()
  createTray()
}

const hasLock = app.requestSingleInstanceLock()
if (!hasLock) {
  app.quit()
} else {
  app.on('second-instance', () => revealWindow())

  app.whenReady().then(boot).catch((error) => {
    dialog.showErrorBox('启动失败', String((error && error.message) || error))
    app.quit()
  })

  app.on('activate', () => {
    // Dock 图标被点：没有窗口就重建，有窗口就从托盘里拿出来
    if (mainWindow && !mainWindow.isDestroyed()) {
      revealWindow()
    } else if (staticServer) {
      createWindow()
    }
  })

  app.on('window-all-closed', () => {
    // macOS 常态是常驻托盘；其它平台按惯例退出
    if (process.platform !== 'darwin') {
      app.quit()
    }
  })

  app.on('before-quit', () => {
    quitting = true
    saveWindowBounds()
  })

  app.on('will-quit', () => {
    if (staticServer) {
      staticServer.close().catch(() => {})
    }
  })
}
