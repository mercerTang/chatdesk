/**
 * 聊天记录本地持久化（IndexedDB）。
 * 只缓存服务端已确认的消息（有 seq）：服务端仍是权威，
 * 打开会话时先读缓存渲染，再按缺口向服务端对账。
 */
const DB_NAME = 'customer-chat'
const DB_VERSION = 1
const STORE = 'messages'

/** 本地缓存与服务端一致的保留天数。 */
export const RETENTION_DAYS = 30

/** 是否已超过保留期（按消息发送时间）。 */
export function isExpired(time) {
  const sentAt = Date.parse(time)
  return Number.isFinite(sentAt) && sentAt < Date.now() - RETENTION_DAYS * 24 * 60 * 60 * 1000
}

let dbPromise = null

function openDb() {
  if (typeof indexedDB === 'undefined') {
    return Promise.resolve(null)
  }
  if (!dbPromise) {
    dbPromise = new Promise((resolve) => {
      const request = indexedDB.open(DB_NAME, DB_VERSION)
      request.onupgradeneeded = () => {
        const db = request.result
        if (!db.objectStoreNames.contains(STORE)) {
          const store = db.createObjectStore(STORE, { keyPath: 'key' })
          store.createIndex('byConversation', ['accountId', 'conversationId', 'seq'])
        }
      }
      request.onsuccess = () => resolve(request.result)
      request.onerror = () => resolve(null)
    })
  }
  return dbPromise
}

function keyOf(accountId, conversationId, seq) {
  return `${accountId}:${conversationId}:${seq}`
}

/** 读取某账号在某会话下的全部本地消息，按 seq 升序。 */
export async function loadMessages(accountId, conversationId) {
  const db = await openDb()
  if (!db) {
    return []
  }
  return new Promise((resolve) => {
    try {
      const tx = db.transaction(STORE, 'readonly')
      const index = tx.objectStore(STORE).index('byConversation')
      const range = IDBKeyRange.bound(
        [accountId, conversationId, 0],
        [accountId, conversationId, Number.MAX_SAFE_INTEGER]
      )
      const request = index.getAll(range)
      request.onsuccess = () => resolve((request.result || []).sort((a, b) => a.seq - b.seq))
      request.onerror = () => resolve([])
    } catch {
      resolve([])
    }
  })
}

/** 写入/更新一条已确认消息（同 key 覆盖，回执状态随更新落库）。 */
export async function saveMessage(accountId, conversationId, item) {
  if (!item?.seq) {
    return
  }
  const db = await openDb()
  if (!db) {
    return
  }
  await new Promise((resolve) => {
    try {
      const tx = db.transaction(STORE, 'readwrite')
      tx.objectStore(STORE).put({
        key: keyOf(accountId, conversationId, item.seq),
        accountId,
        conversationId,
        seq: item.seq,
        senderId: item.senderId,
        senderName: item.senderName || '',
        content: item.content,
        time: item.time,
        state: item.state || ''
      })
      tx.oncomplete = resolve
      tx.onerror = resolve
      tx.onabort = resolve
    } catch {
      resolve()
    }
  })
}

/** 清理某账号超过保留天数的本地消息（默认与服务端一致：30 天）。 */
export async function purgeOlderThan(accountId, retentionDays = RETENTION_DAYS) {
  const db = await openDb()
  if (!db) {
    return
  }
  const cutoff = Date.now() - retentionDays * 24 * 60 * 60 * 1000
  await new Promise((resolve) => {
    try {
      const tx = db.transaction(STORE, 'readwrite')
      const request = tx.objectStore(STORE).openCursor()
      request.onsuccess = () => {
        const cursor = request.result
        if (!cursor) {
          return
        }
        const record = cursor.value
        const sentAt = Date.parse(record.time)
        if (record.accountId === accountId && Number.isFinite(sentAt) && sentAt < cutoff) {
          cursor.delete()
        }
        cursor.continue()
      }
      tx.oncomplete = resolve
      tx.onerror = resolve
      tx.onabort = resolve
    } catch {
      resolve()
    }
  })
}
