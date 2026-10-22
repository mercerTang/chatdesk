import { reactive } from 'vue'

const KEY = 'chat.theme'

function readTheme() {
  try {
    return localStorage.getItem(KEY) === 'light' ? 'light' : 'dark'
  } catch {
    return 'dark'
  }
}

export const theme = reactive({ current: readTheme() })

function apply() {
  document.documentElement.classList.toggle('dark', theme.current === 'dark')
}

apply()

export function setTheme(next) {
  theme.current = next === 'light' ? 'light' : 'dark'
  try {
    localStorage.setItem(KEY, theme.current)
  } catch {
    /* 隐私模式下忽略 */
  }
  apply()
}

export function toggleTheme() {
  setTheme(theme.current === 'dark' ? 'light' : 'dark')
}
