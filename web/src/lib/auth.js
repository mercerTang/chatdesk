import { computed, reactive } from 'vue'

const TOKEN_KEY = 'chat.token'
const USER_KEY = 'chat.user'

function readUser() {
  try {
    const raw = localStorage.getItem(USER_KEY)
    return raw ? JSON.parse(raw) : null
  } catch {
    return null
  }
}

/** 登录态：token 与账号信息都存 localStorage，刷新后仍可恢复。 */
export const auth = reactive({
  token: localStorage.getItem(TOKEN_KEY) || '',
  user: readUser()
})

export const isLoggedIn = computed(() => Boolean(auth.token && auth.user))

/** 按角色给落地页：访客 → /chat，客服 → /agent，管理员 → /admin。 */
export function homePath(user = auth.user) {
  if (user?.role === 'admin') {
    return '/admin'
  }
  return user?.role === 'agent' ? '/agent' : '/chat'
}

export function setSession(payload) {
  auth.token = payload.token
  auth.user = payload.user
  localStorage.setItem(TOKEN_KEY, payload.token)
  localStorage.setItem(USER_KEY, JSON.stringify(payload.user))
}

/** 用服务端最新资料更新本地登录态（如客服展示名变更后，无需重新登录）。 */
export function updateUser(patch) {
  if (!auth.user || !patch) {
    return
  }
  Object.assign(auth.user, patch)
  localStorage.setItem(USER_KEY, JSON.stringify(auth.user))
}

export function clearSession() {
  auth.token = ''
  auth.user = null
  localStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(USER_KEY)
}
