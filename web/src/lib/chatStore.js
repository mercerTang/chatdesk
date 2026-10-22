import { reactive } from 'vue'
import { HubConnectionState } from '@microsoft/signalr'
import { api } from './api'
import { createChatConnection } from './signalr'
import { newClientMessageId } from './format'
import { loadMessages, saveMessage, purgeOlderThan, isExpired } from './localDb'

/** 单次补拉条数：一批拉满说明后面还有，继续拉直到排空。 */
export const PAGE_SIZE = 200
const RETRY_INTERVAL_MS = 5000

/**
 * 会话消息引擎（访客端与客服台共用）：
 * - 消息缓存按会话保存，服务端 Seq 去重、ClientMessageId 合并乐观消息；
 * - 收到 Seq 缺口先补拉，补拉循环排空到没有缺口为止；
 * - 三段回执：已发送（回执）/ 已送达 / 已读；回执按 byAccountId 过滤，绝不标记自己的消息；
 * - 断线时按会话保留发件箱，重连后按原 ClientMessageId 补发（服务端幂等）。
 */
export function createChatStore({ selfId, hooks = {} }) {
  const channels = reactive(new Map())
  const state = reactive({ connection: 'idle' })

  // 本地缓存与服务端同一保留策略：30 天
  purgeOlderThan(selfId).catch(() => {})

  let connection = null
  let retryTimer = null
  let starting = false

  const notify = (error) => {
    hooks.onError?.(error instanceof Error ? error : new Error(String(error)))
  }

  function channelFor(conversationId) {
    let channel = channels.get(conversationId)
    if (!channel) {
      channel = reactive({
        id: conversationId,
        status: 'active',
        items: [],
        lastSeq: 0,
        outbox: [],
        joined: false,
        pulling: null,
        hydrated: false
      })
      channels.set(conversationId, channel)
    }
    return channel
  }

  function items(conversationId) {
    return conversationId ? channels.get(conversationId)?.items ?? [] : []
  }

  function isConnected() {
    return Boolean(connection) && connection.state === HubConnectionState.Connected
  }

  function persist(channel, item) {
    if (!item?.seq) {
      return
    }
    saveMessage(selfId, channel.id, item).catch(() => {})
  }

  // ---------------------------------------------------------------- 消息

  function applyServerFields(item, dto) {
    item.key = `seq-${dto.seq}`
    item.seq = dto.seq
    item.content = dto.content
    item.senderId = dto.senderId
    item.senderName = dto.senderName
    item.self = dto.senderId === selfId
    item.time = dto.sentAt
    item.state = item.self ? (dto.readAt ? 'read' : dto.deliveredAt ? 'delivered' : 'sent') : ''
  }

  /** 落一条服务端消息：同 Seq 忽略，命中本地乐观消息则合并。 */
  function applyMessage(channel, dto) {
    if (!dto) {
      return
    }
    if (dto.seq && channel.items.some((entry) => entry.seq === dto.seq)) {
      return
    }

    // 超过保留期的消息不展示也不落本地，只推进水位避免反复补拉
    if (dto.seq && isExpired(dto.sentAt)) {
      if (dto.seq > channel.lastSeq) {
        channel.lastSeq = dto.seq
      }
      return
    }

    const pending = dto.clientMessageId
      ? channel.items.find((entry) => !entry.seq && entry.clientMessageId === dto.clientMessageId)
      : null

    let stored
    if (pending) {
      applyServerFields(pending, dto)
      stored = pending
    } else {
      const item = reactive({ key: '', clientMessageId: dto.clientMessageId, seq: 0 })
      applyServerFields(item, dto)
      channel.items.push(item)
      stored = item
    }
    persist(channel, stored)

    if (dto.seq > channel.lastSeq) {
      channel.lastSeq = dto.seq
    }
  }

  function applyAck(channel, ack) {
    const item =
      channel.items.find(
        (entry) => entry.clientMessageId === ack.clientMessageId && (!entry.seq || entry.seq === ack.seq)
      ) || channel.items.find((entry) => entry.clientMessageId === ack.clientMessageId)
    if (!item) {
      return
    }

    item.key = `seq-${ack.seq}`
    item.seq = ack.seq
    item.time = ack.sentAt || item.time
    if (item.state !== 'read' && item.state !== 'delivered') {
      item.state = 'sent'
    }
    persist(channel, item)
    if (ack.seq > channel.lastSeq) {
      channel.lastSeq = ack.seq
    }
  }

  /** 增量补拉：从 fromSeq 起循环拉满 PAGE_SIZE 的批次，直到排空。 */
  async function pull(channel, fromSeq = channel.lastSeq) {
    if (channel.pulling) {
      return channel.pulling
    }

    channel.pulling = (async () => {
      try {
        let cursor = fromSeq
        for (;;) {
          const batch = await api.messages(channel.id, cursor, PAGE_SIZE)
          if (!batch.length) {
            break
          }
          batch.forEach((dto) => {
            applyMessage(channel, dto)
            if (dto.seq > cursor) {
              cursor = dto.seq
            }
          })
          if (batch.length < PAGE_SIZE) {
            break
          }
        }
      } catch (error) {
        notify(error)
      } finally {
        channel.pulling = null
      }
    })()

    return channel.pulling
  }

  /** 确认收到对端消息（服务端只更新对端消息，天然不会标记自己的）。 */
  function acknowledgeDelivered(channel, upToSeq) {
    if (!isConnected() || !upToSeq) {
      return
    }
    connection.invoke('AckDelivered', channel.id, upToSeq).catch(() => {})
  }

  /** 确认已读：只在会话处于前台（被打开）时调用，后台会话不标记已读。 */
  function acknowledgeRead(channel, upToSeq) {
    if (!isConnected() || !upToSeq) {
      return
    }
    connection.invoke('AckRead', channel.id, upToSeq).catch(() => {})
  }

  let visibleId = null

  // ---------------------------------------------------------------- 实时事件

  async function handleIncoming(dto) {
    hooks.onMessage?.(dto)

    const channel = channels.get(dto.conversationId)
    if (!channel) {
      // 未打开的会话：至少确认送达（客户端确实收到了），已读留到用户打开会话时再确认
      if (isConnected() && dto.seq) {
        connection.invoke('AckDelivered', dto.conversationId, dto.seq).catch(() => {})
      }
      return
    }

    // 跳号检测：先补拉把缺口排空，再落这一条
    if (dto.seq > channel.lastSeq + 1) {
      await pull(channel)
    }

    applyMessage(channel, dto)
    acknowledgeDelivered(channel, channel.lastSeq)
    if (visibleId === channel.id) {
      acknowledgeRead(channel, channel.lastSeq)
    }
  }

  function handleDelivered(payload) {
    const channel = channels.get(payload.conversationId)
    if (!channel) {
      return
    }
    // 自己发出的确认回执不能用来标记自己的消息
    if (payload.byAccountId === selfId) {
      return
    }
    channel.items.forEach((item) => {
      if (item.self && item.seq && item.seq <= payload.upToSeq && item.state === 'sent') {
        item.state = 'delivered'
        persist(channel, item)
      }
    })
  }

  function handleRead(payload) {
    const channel = channels.get(payload.conversationId)
    if (!channel) {
      return
    }
    if (payload.byAccountId === selfId) {
      return
    }
    channel.items.forEach((item) => {
      if (
        item.self &&
        item.seq &&
        item.seq <= payload.upToSeq &&
        item.state !== 'failed' &&
        item.state !== 'read'
      ) {
        item.state = 'read'
        persist(channel, item)
      }
    })
  }

  // ---------------------------------------------------------------- 连接

  async function join(channel) {
    if (!isConnected()) {
      return false
    }
    try {
      await connection.invoke('JoinConversation', channel.id)
      channel.joined = true
      return true
    } catch (error) {
      // 会话可能已关闭或不再属于当前账号，本地缓存仍然可读
      notify(error)
      return false
    }
  }

  async function flushOutbox(channel) {
    if (!isConnected() || channel.outbox.length === 0) {
      return
    }

    const pending = channel.outbox.splice(0, channel.outbox.length)
    for (const entry of pending) {
      try {
        const ack = await connection.invoke('SendMessage', channel.id, entry.content, entry.clientMessageId)
        applyAck(channel, ack)
      } catch (error) {
        channel.outbox.push(entry)
        const item = channel.items.find((entryItem) => entryItem.clientMessageId === entry.clientMessageId)
        if (item) {
          item.state = 'failed'
        }
      }
    }
  }

  /** 重连后：重新入组 → 按本地水位补拉 → 补发发件箱。 */
  async function resync() {
    for (const channel of [...channels.values()]) {
      await join(channel)
      await pull(channel)
      await flushOutbox(channel)
      acknowledgeDelivered(channel, channel.lastSeq)
      if (visibleId === channel.id) {
        acknowledgeRead(channel, channel.lastSeq)
      }
    }
    hooks.onRestored?.()
  }

  async function tryStart() {
    if (!connection || starting || connection.state !== HubConnectionState.Disconnected) {
      return
    }
    starting = true
    state.connection = 'connecting'
    try {
      await connection.start()
      state.connection = 'connected'
      await resync()
    } catch (error) {
      state.connection = 'offline'
      notify(error)
      scheduleRetry()
    } finally {
      starting = false
    }
  }

  function scheduleRetry() {
    if (retryTimer) {
      return
    }
    retryTimer = setTimeout(() => {
      retryTimer = null
      tryStart()
    }, RETRY_INTERVAL_MS)
  }

  function registerHandlers(conn) {
    conn.on('ReceiveMessage', (message) => {
      handleIncoming(message).catch(notify)
    })
    conn.on('MessageDelivered', handleDelivered)
    conn.on('MessageRead', handleRead)
    conn.on('AgentPresenceChanged', (payload) => hooks.onPresenceChanged?.(payload))
    conn.on('ConversationCreated', (conversation) => hooks.onConversationCreated?.(conversation))
    conn.on('ConversationClosed', (payload) => {
      const channel = channels.get(payload.conversationId)
      if (channel) {
        channel.status = 'closed'
      }
      hooks.onConversationClosed?.(payload)
    })
    conn.on('ConversationTransferred', (payload) => hooks.onConversationTransferred?.(payload))
    conn.on('ConversationAssigned', (conversation) => hooks.onConversationAssigned?.(conversation))
    // 账号被管理员停用：由页面自行清会话并回登录页（服务端不断连，避免打断进行中的会话）
    conn.on('SessionRevoked', () => hooks.onSessionRevoked?.())

    conn.onreconnecting(() => {
      state.connection = 'reconnecting'
    })
    conn.onreconnected(() => {
      state.connection = 'connected'
      resync().catch(notify)
    })
    conn.onclose(() => {
      state.connection = 'offline'
      hooks.onClosed?.()
      scheduleRetry()
    })
  }

  async function connect() {
    if (!connection) {
      connection = createChatConnection()
      registerHandlers(connection)
    }
    await tryStart()
  }

  function dispose() {
    if (retryTimer) {
      clearTimeout(retryTimer)
      retryTimer = null
    }
    if (connection) {
      const current = connection
      connection = null
      current.stop().catch(() => {})
    }
    state.connection = 'idle'
  }

  // ---------------------------------------------------------------- 对外操作

  /** 从 IndexedDB 读本地历史，先渲染再对账。 */
  async function hydrate(channel) {
    if (channel.hydrated) {
      return
    }
    channel.hydrated = true
    const cached = await loadMessages(selfId, channel.id)
    for (const record of cached) {
      if (channel.items.some((entry) => entry.seq === record.seq)) {
        continue
      }
      if (isExpired(record.time)) {
        continue
      }
      const item = reactive({
        key: `seq-${record.seq}`,
        clientMessageId: null,
        seq: record.seq,
        content: record.content,
        senderId: record.senderId,
        senderName: record.senderName,
        self: record.senderId === selfId,
        time: record.time,
        state: record.state || ''
      })
      channel.items.push(item)
      if (record.seq > channel.lastSeq) {
        channel.lastSeq = record.seq
      }
    }
    channel.items.sort((a, b) => (a.seq || Number.MAX_SAFE_INTEGER) - (b.seq || Number.MAX_SAFE_INTEGER))
  }

  /** 本地缓存是否从 seq=1 连续；不连续说明缺段，需要从 0 对账。 */
  function cacheIsContiguous(channel) {
    let expected = 1
    for (const item of channel.items) {
      if (!item.seq) {
        continue
      }
      if (item.seq !== expected) {
        return false
      }
      expected += 1
    }
    return expected > 1
  }

  /** 打开会话：入组（在线时）、补拉历史、确认送达与已读（该会话进入前台）。 */
  async function open(conversation) {
    const channel = channelFor(conversation.id)
    channel.status = conversation.status || 'active'
    channel.joined = false
    visibleId = conversation.id
    await hydrate(channel)
    // 本地缓存缺段（或为空）时从头对账，避免水位跳过缺失消息
    const fromSeq = cacheIsContiguous(channel) ? channel.lastSeq : 0
    await join(channel)
    await pull(channel, fromSeq)
    acknowledgeDelivered(channel, channel.lastSeq)
    acknowledgeRead(channel, channel.lastSeq)
    return channel
  }

  /** 发送：在线直接发，离线进该会话的发件箱，重连后按原幂等键补发。 */
  async function send(conversationId, text) {
    const content = text.trim()
    if (!content) {
      return null
    }

    const channel = channelFor(conversationId)
    if (channel.status !== 'active') {
      throw new Error('会话已关闭，无法继续发送')
    }

    const clientMessageId = newClientMessageId()
    const item = reactive({
      key: `local-${clientMessageId}`,
      clientMessageId,
      seq: 0,
      content,
      senderId: selfId,
      senderName: '',
      self: true,
      time: new Date().toISOString(),
      state: isConnected() ? 'sending' : 'queued'
    })
    channel.items.push(item)

    if (!isConnected()) {
      channel.outbox.push({ clientMessageId, content })
      return item
    }

    try {
      const ack = await connection.invoke('SendMessage', conversationId, content, clientMessageId)
      applyAck(channel, ack)
    } catch (error) {
      item.state = 'failed'
      notify(error)
    }
    return item
  }

  /** 发送失败后重试：在线直接重发，离线放回发件箱。 */
  async function retry(conversationId, item) {
    const channel = channels.get(conversationId)
    if (!channel || !item?.clientMessageId) {
      return
    }

    if (!isConnected()) {
      item.state = 'queued'
      channel.outbox.push({ clientMessageId: item.clientMessageId, content: item.content })
      return
    }

    item.state = 'sending'
    try {
      const ack = await connection.invoke('SendMessage', conversationId, item.content, item.clientMessageId)
      applyAck(channel, ack)
    } catch (error) {
      item.state = 'failed'
      notify(error)
    }
  }

  /** 只建缓存并入组（不补拉）：客服台收到新会话时调用，后续消息就进缓存。 */
  async function track(conversation) {
    const channel = channelFor(conversation.id)
    channel.status = conversation.status || 'active'
    await join(channel)
    return channel
  }

  function setStatus(conversationId, status) {
    const channel = channels.get(conversationId)
    if (channel) {
      channel.status = status
    }
  }

  /** 放弃内存中的会话缓存（工单被转出后调用，避免重连时对无权会话再次入组）。 */
  function forget(conversationId) {
    channels.delete(conversationId)
    if (visibleId === conversationId) {
      visibleId = null
    }
  }

  return {
    state,
    items,
    connect,
    dispose,
    open,
    track,
    send,
    retry,
    setStatus,
    forget,
    isConnected
  }
}
