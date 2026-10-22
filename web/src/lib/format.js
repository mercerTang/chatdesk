/** 时间与消息 ID 的小工具。 */
export function formatTime(value) {
  if (!value) {
    return ''
  }
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return ''
  }
  const sameDay = date.toDateString() === new Date().toDateString()
  return sameDay
    ? date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
    : date.toLocaleString('zh-CN', {
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit'
      })
}

export function newClientMessageId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  return `cmid-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
}

export function initialOf(name) {
  const text = (name || '').trim()
  if (!text) {
    return '客'
  }
  // 种子里客服展示名带「客服」前缀，取去掉前缀后的末字（小美 → 美）更易区分
  const stripped = text.replace(/^客服/, '') || text
  if (/^[\x00-\x7F]+$/.test(stripped)) {
    return stripped[0].toUpperCase()
  }
  return stripped.length > 1 ? stripped.slice(-1) : stripped
}
