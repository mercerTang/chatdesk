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
    path: '/admin',
    component: () => import('../views/admin/AdminLayout.vue'),
    meta: { role: 'admin' },
    children: [
      {
        path: '',
        name: 'admin-dashboard',
        component: () => import('../views/admin/AdminDashboard.vue')
      },
      {
        path: 'agents',
        name: 'admin-agents',
        component: () => import('../views/admin/AdminAgents.vue')
      },
      {
        path: 'conversations',
        name: 'admin-conversations',
        component: () => import('../views/admin/AdminConversations.vue')
      },
      {
        path: 'tickets',
        name: 'admin-tickets',
        component: () => import('../views/admin/AdminTickets.vue')
      }
    ]
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

// 角色守卫：未登录 → /login；访客只进 /chat，客服只进 /agent，管理员只进 /admin
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
