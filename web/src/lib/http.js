import axios from 'axios'
import router from '../router'
import { auth, clearSession } from './auth'

// 缺省用相对路径：开发期走 Vite 代理，生产期由反向代理把 /api、/hubs 转到后端
const baseURL = import.meta.env.VITE_API_BASE_URL || ''

export const http = axios.create({ baseURL, timeout: 20000 })

http.interceptors.request.use((config) => {
  if (auth.token) {
    config.headers.Authorization = `Bearer ${auth.token}`
  }
  return config
})

http.interceptors.response.use(
  (response) => response,
  (error) => {
    const status = error.response?.status
    const message = error.response?.data?.error || error.message || '请求失败'

    // 令牌失效：清登录态并回登录页，避免停留在半登录状态
    if (status === 401) {
      clearSession()
      if (router.currentRoute.value.path !== '/login') {
        router.replace({ path: '/login', query: { redirect: router.currentRoute.value.fullPath } })
      }
    }

    return Promise.reject(new Error(message))
  }
)
