/** 新消息提醒：标签页不可见时闪烁标题；回到页面自动停止。 */
let flashTimer = null
const baseTitle = document.title

export function flashTitle(text = '新消息') {
  if (!document.hidden || flashTimer) {
    return
  }
  let on = false
  flashTimer = setInterval(() => {
    on = !on
    document.title = on ? `【${text}】${baseTitle}` : baseTitle
  }, 1000)
}

export function stopFlash() {
  if (flashTimer) {
    clearInterval(flashTimer)
    flashTimer = null
  }
  document.title = baseTitle
}

document.addEventListener('visibilitychange', () => {
  if (!document.hidden) {
    stopFlash()
  }
})
