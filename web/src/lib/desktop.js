/**
 * 桌面端桥与应用内通知的统一入口。
 * 浏览器里没有 window.desktop，所有函数自动退回网页行为，页面代码不需要判断运行环境。
 */

const API_BASE_PARAM = 'apiBase'
const API_BASE_KEY = 'chat.apiBase'

/** 去掉首尾空白与结尾斜杠：拼接 `/api`、`/hubs/chat` 时不能出现双斜杠。 */
function normalizeBase(value) {
  return String(value == null ? '' : value)
    .trim()
    .replace(/\/+$/, '')
}

function readStoredBase() {
  try {
    return localStorage.getItem(API_BASE_KEY) || ''
  } catch {
    // 隐私模式下 localStorage 读写会抛，按没有存过处理
    return ''
  }
}

function rememberBase(value) {
  try {
    localStorage.setItem(API_BASE_KEY, value)
  } catch {
    // 写不进去不影响本次会话的使用
  }
}

/** 是否运行在桌面客户端里（preload 暴露了 window.desktop）。 */
export function hasDesktopBridge() {
  return Boolean(window.desktop && typeof window.desktop.notify === 'function')
}

/**
 * 后端基址，顺序固定：
 * 1. URL 参数 ?apiBase=（桌面端主进程每次加载页面都带当前配置）
 * 2. localStorage（上次解析到的地址；路由跳转后 URL 参数会丢，靠它接续）
 * 3. 构建期 VITE_API_BASE_URL
 * 4. 同源空串（开发期走 Vite 代理，生产期走反向代理）
 */
export function resolveApiBase() {
  const fromUrl = normalizeBase(new URLSearchParams(window.location.search).get(API_BASE_PARAM))
  if (fromUrl) {
    rememberBase(fromUrl)
    return fromUrl
  }

  const stored = normalizeBase(readStoredBase())
  if (stored) {
    return stored
  }

  return normalizeBase(import.meta.env.VITE_API_BASE_URL || '')
}

/**
 * 发系统通知：桌面端走主进程（点通知聚焦窗口），返回 true；
 * 返回 false 表示没有桥，调用方退回 Element Plus 的应用内通知。
 */
export function notifyDesktop({ title, body } = {}) {
  if (!hasDesktopBridge()) {
    return false
  }
  const payload = { title: String(title || 'ChatDesk 客服'), body: String(body || '') }
  const pending = window.desktop.notify(payload)
  if (pending && typeof pending.catch === 'function') {
    pending.catch(() => {})
  }
  return true
}
