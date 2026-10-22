'use strict'

/**
 * 桌面端内置静态服务器：托管 web/dist，给 history 路由兜底回退 index.html。
 *
 * 为什么不用 file://：Vue Router 是 history 模式，file:// 下刷新 /agent 直接 404；
 * 用 http://127.0.0.1:端口 托管后，页面有正常的 origin，前端请求与 SignalR 按正常同源/跨域规则走。
 */

const fs = require('node:fs')
const http = require('node:http')
const path = require('node:path')

const HOST = '127.0.0.1'

const MIME_TYPES = new Map([
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.mjs', 'text/javascript; charset=utf-8'],
  ['.css', 'text/css; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.map', 'application/json; charset=utf-8'],
  ['.svg', 'image/svg+xml'],
  ['.png', 'image/png'],
  ['.jpg', 'image/jpeg'],
  ['.jpeg', 'image/jpeg'],
  ['.gif', 'image/gif'],
  ['.webp', 'image/webp'],
  ['.ico', 'image/x-icon'],
  ['.woff', 'font/woff'],
  ['.woff2', 'font/woff2'],
  ['.ttf', 'font/ttf'],
  ['.txt', 'text/plain; charset=utf-8']
])

/**
 * 请求路径 → 磁盘路径。越出 root 的路径（../ 之类）返回 null，调用方按未命中处理。
 */
function resolveFile(root, pathname) {
  let decoded
  try {
    decoded = decodeURIComponent(pathname)
  } catch {
    // 非法百分号编码：当作未命中，交给 index.html 兜底
    return null
  }
  const target = path.join(root, decoded)
  const relative = path.relative(root, target)
  if (relative.startsWith('..') || path.isAbsolute(relative)) {
    return null
  }
  return target
}

/**
 * 命中真实文件才返回 true；目录或不存在返回 false，由调用方走路由回退。
 */
function sendFile(req, res, filePath) {
  let stat
  try {
    stat = fs.statSync(filePath)
  } catch {
    return false
  }
  if (!stat.isFile()) {
    return false
  }

  // 构建产物里的 assets/* 带内容哈希，可以长缓存；其余（index.html）不缓存，重载才拿到新入口
  const cached = filePath.split(path.sep).includes('assets')
  res.writeHead(200, {
    'Content-Type': MIME_TYPES.get(path.extname(filePath).toLowerCase()) || 'application/octet-stream',
    'Content-Length': stat.size,
    'Cache-Control': cached ? 'public, max-age=31536000, immutable' : 'no-store',
    'X-Content-Type-Options': 'nosniff'
  })
  if (req.method === 'HEAD') {
    res.end()
    return true
  }
  const stream = fs.createReadStream(filePath)
  stream.on('error', () => res.destroy())
  stream.pipe(res)
  return true
}

function createHandler(root) {
  const indexFile = path.join(root, 'index.html')

  return (req, res) => {
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      res.writeHead(405, { Allow: 'GET, HEAD', 'Content-Type': 'text/plain; charset=utf-8' })
      res.end('Method Not Allowed')
      return
    }

    let pathname = '/'
    try {
      pathname = new URL(req.url, `http://${HOST}`).pathname
    } catch {
      // 解析失败按根路径处理
    }

    const target = resolveFile(root, pathname)
    if (target && sendFile(req, res, target)) {
      return
    }

    // history 路由回退：/agent、/tickets 这类前端路由刷新时都回 index.html
    if (sendFile(req, res, indexFile)) {
      return
    }

    res.writeHead(500, { 'Content-Type': 'text/plain; charset=utf-8' })
    res.end('缺少 index.html：请先在 web 目录执行 npm run build')
  }
}

function listen(server, port) {
  return new Promise((resolve, reject) => {
    const onError = (error) => {
      server.removeListener('listening', onListening)
      reject(error)
    }
    const onListening = () => {
      server.removeListener('error', onError)
      resolve(server.address().port)
    }
    server.once('error', onError)
    server.once('listening', onListening)
    server.listen(port, HOST)
  })
}

/**
 * 启动静态服务器。
 * preferredPort 是「优先端口」：端口稳定，页面的 origin 才稳定，localStorage（登录态）才能跨重启保留；
 * 被占用时退回系统随机端口，本次会话可用，代价是登录态要从头开始。
 */
async function startStaticServer({ root, preferredPort = 0 }) {
  const rootPath = path.resolve(root)
  const server = http.createServer(createHandler(rootPath))

  let port
  try {
    port = await listen(server, preferredPort)
  } catch (error) {
    if (error.code !== 'EADDRINUSE') {
      throw error
    }
    port = await listen(server, 0)
  }

  return {
    server,
    port,
    url: `http://${HOST}:${port}/`,
    close: () => new Promise((resolve) => server.close(resolve))
  }
}

module.exports = { startStaticServer, createHandler, resolveFile }
