import { createRouter, createWebHistory } from 'vue-router'
import { auth, homePath } from '../lib/auth'

const routes = [
  {
    path: '/',
    redirect: () => homePath()
  },
  {
    path: '/login',
    name: 'login',
    component: () => import('../views/Login.vue'),
    meta: { public: true }
  },
  {
    path: '/chat',
    name: 'visitor-chat',
    component: () => import('../views/VisitorChat.vue'),
    meta: { role: 'visitor' }
  },
  {
    path: '/agent',
    name: 'agent-console',
    component: () => import('../views/AgentConsole.vue'),
    meta: { role: 'agent' }
  },
  {
    path: '/tickets',
    name: 'tickets',
    component: () => import('../views/Tickets.vue'),
    meta: { role: 'agent' }
  },
  {
    path: '/:pathMatch(.*)*',
    redirect: '/'
  }
]

const router = createRouter({
  history: createWebHistory(),
  routes
})

// 角色守卫：未登录 → /login；访客只进 /chat，客服只进 /agent
router.beforeEach((to) => {
  const loggedIn = Boolean(auth.token && auth.user)

  if (to.meta.public) {
    return loggedIn ? homePath() : true
  }

  if (!loggedIn) {
    return { path: '/login', query: { redirect: to.fullPath } }
  }

  if (to.meta.role && to.meta.role !== auth.user.role) {
    return homePath()
  }

  return true
})

export default router
