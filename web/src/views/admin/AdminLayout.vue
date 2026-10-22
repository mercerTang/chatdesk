<script setup>
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import BrandMark from '../../components/BrandMark.vue'
import ThemeSwitch from '../../components/ThemeSwitch.vue'
import { auth, clearSession } from '../../lib/auth'

const route = useRoute()
const router = useRouter()
const me = auth.user

const menus = [
  { path: '/admin', label: '仪表盘', hint: '全局概况' },
  { path: '/admin/agents', label: '客服管理', hint: '账号、并发与停用' },
  { path: '/admin/conversations', label: '会话总览', hint: '跨客服全部会话' },
  { path: '/admin/tickets', label: '工单总览', hint: '任意工单与强制转派' }
]

// 子页高亮所属菜单；/admin 只匹配精确路径，避免吃掉其它项的匹配
const activePath = computed(() => {
  const matched = menus.filter(
    (menu) =>
      route.path === menu.path ||
      (menu.path !== '/admin' && route.path.startsWith(`${menu.path}/`))
  )
  return matched.length ? matched[matched.length - 1].path : '/admin'
})

function logout() {
  clearSession()
  router.replace('/login')
}
</script>

<template>
  <div class="page">
    <header class="topbar">
      <div class="brand-lockup">
        <BrandMark />
        <el-tag size="small" effect="plain">管理后台</el-tag>
      </div>
      <div class="topbar-right">
        <ThemeSwitch />
        <span class="user">{{ me.displayName }}（{{ me.username }}）</span>
        <el-button text size="small" @click="logout">退出登录</el-button>
      </div>
    </header>

    <div class="admin-body">
      <aside class="admin-nav">
        <nav>
          <button
            v-for="menu in menus"
            :key="menu.path"
            type="button"
            class="nav-item"
            :class="{ 'is-active': activePath === menu.path }"
            @click="router.push(menu.path)"
          >
            <span class="nav-label">{{ menu.label }}</span>
            <span class="nav-hint">{{ menu.hint }}</span>
          </button>
        </nav>

        <div class="nav-foot">
          <el-button text size="small" @click="router.push('/')">返回工作台</el-button>
        </div>
      </aside>

      <main class="admin-main">
        <router-view />
      </main>
    </div>
  </div>
</template>
