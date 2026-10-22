import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { auth } from './auth'

const hubUrl = `${import.meta.env.VITE_API_BASE_URL || ''}/hubs/chat`

/**
 * SignalR 连接：JWT 走 access_token 查询参数（服务端 Program.cs 已配置）。
 * 自带自动重连只覆盖短窗口，长断线的兜底重连在 chatStore 里做。
 */
export function createChatConnection() {
  return new HubConnectionBuilder()
    .withUrl(hubUrl, { accessTokenFactory: () => auth.token })
    .withAutomaticReconnect([0, 1000, 2500, 5000, 10000])
    .configureLogging(LogLevel.Warning)
    .build()
}
