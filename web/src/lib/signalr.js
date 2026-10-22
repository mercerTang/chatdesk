import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { auth } from './auth'
import { resolveApiBase } from './desktop'

const hubUrl = `${resolveApiBase()}/hubs/chat`

/**
 * SignalR 连接：JWT 走 access_token 查询参数（服务端 Program.cs 已配置）。
 * 自带自动重连只覆盖短窗口，长断线的兜底重连在 chatStore 里做。
 *
 * withCredentials: false 是跨域直达后端时必须的：客户端默认带凭据（fetch 用 credentials: include），
 * 服务端 CORS 是 AllowAnyOrigin（返回 *），带凭据的请求会被浏览器整条拒掉（Failed to fetch）。
 * 桌面端页面在 127.0.0.1 上、接口在后端地址上，正是跨域；鉴权只靠 access_token，不需要 Cookie。
 */
export function createChatConnection() {
  return new HubConnectionBuilder()
    .withUrl(hubUrl, { accessTokenFactory: () => auth.token, withCredentials: false })
    .withAutomaticReconnect([0, 1000, 2500, 5000, 10000])
    .configureLogging(LogLevel.Warning)
    .build()
}
