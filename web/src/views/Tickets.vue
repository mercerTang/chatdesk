<script setup>
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { useRouter } from 'vue-router'
import BrandMark from '../components/BrandMark.vue'
import EmptyState from '../components/EmptyState.vue'
import ThemeSwitch from '../components/ThemeSwitch.vue'
import { api } from '../lib/api'
import { auth, clearSession, updateUser } from '../lib/auth'
import { isExpired } from '../lib/localDb'
import { formatTime } from '../lib/format'

const router = useRouter()
const me = auth.user

const tickets = ref([])
const current = ref(null)
const messages = ref([])
const loading = ref(false)
const loadingDetail = ref(false)
const exporting = ref('')
const retentionDays = ref(30)

const statusText = (status) => (status === 'open' ? '进行中' : '已结束')
const statusType = (status) => (status === 'open' ? 'primary' : 'info')

async function loadTickets() {
  loading.value = true
  try {
    tickets.value = await api.tickets()
    if (!current.value && tickets.value.length) {
      await openTicket(tickets.value[0])
    }
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    loading.value = false
  }
}

async function openTicket(ticket) {
  current.value = ticket
  loadingDetail.value = true
  try {
    const detail = await api.ticket(ticket.id)
    current.value = detail.ticket
    messages.value = detail.messages.filter((message) => !isExpired(message.sentAt))
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    loadingDetail.value = false
  }
}

async function exportTicket(format) {
  if (!current.value) {
    return
  }
  exporting.value = format
  try {
    const blob = await api.exportTicket(current.value.id, format)
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `${current.value.ticketNo}.${format}`
    document.body.appendChild(link)
    link.click()
    link.remove()
    URL.revokeObjectURL(url)
    ElMessage.success(`已导出 ${format.toUpperCase()}`)
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    exporting.value = ''
  }
}

function logout() {
  clearSession()
  router.replace('/login')
}

onMounted(async () => {
  try {
    updateUser(await api.me())
  } catch {
    /* 资料同步失败不影响工单查看 */
  }
  try {
    const meta = await api.ticketMeta()
    if (meta?.retentionDays) {
      retentionDays.value = meta.retentionDays
    }
  } catch {
    /* 保留天数取不到时使用默认 30 天 */
  }
  await loadTickets()
})
</script>

<template>
  <div class="page">
    <header class="topbar">
      <BrandMark />
      <div class="topbar-right">
        <ThemeSwitch />
        <el-button text size="small" @click="router.push('/agent')">返回工作台</el-button>
        <span class="user">{{ me.displayName }}（{{ me.username }}）</span>
        <el-button text size="small" @click="logout">退出登录</el-button>
      </div>
    </header>

    <main class="layout">
      <aside class="sidebar">
        <div class="sidebar-head">
          <span>工单记录</span>
          <el-button text size="small" :loading="loading" @click="loadTickets">刷新</el-button>
        </div>

        <div class="sidebar-note">按创建时间倒序，每次会话对应一张工单；聊天记录自发送起保留 {{ retentionDays }} 天</div>

        <div v-loading="loading" class="list">
          <div v-if="!tickets.length" class="list-empty">
            <EmptyState title="还没有工单" hint="访客发起对话后会自动生成工单。" />
          </div>

          <button
            v-for="ticket in tickets"
            :key="ticket.id"
            type="button"
            class="item"
            :class="{ 'is-active': current && current.id === ticket.id }"
            @click="openTicket(ticket)"
          >
            <div class="item-body">
              <div class="item-title">
                <span>{{ ticket.ticketNo }}</span>
                <el-tag size="small" :type="statusType(ticket.status)" effect="plain">
                  {{ statusText(ticket.status) }}
                </el-tag>
              </div>
              <div class="item-sub">{{ ticket.visitorName }} → {{ ticket.agentName }}</div>
              <div class="item-sub">{{ ticket.lastMessage || '暂无消息' }}</div>
            </div>
            <div class="item-side">
              <span>{{ formatTime(ticket.createdAt) }}</span>
              <span>{{ ticket.messageCount }} 条</span>
            </div>
          </button>
        </div>
      </aside>

      <section class="chat-pane">
        <template v-if="current">
          <div class="chat-head">
            <div>
              <div class="chat-title">{{ current.ticketNo }}</div>
              <div class="chat-sub">
                {{ current.visitorName }} → {{ current.agentName }} · {{ statusText(current.status) }}
              </div>
            </div>
            <div class="chat-head-actions">
              <el-button size="small" :loading="exporting === 'txt'" @click="exportTicket('txt')">
                导出 TXT
              </el-button>
              <el-button
                size="small"
                type="primary"
                :loading="exporting === 'csv'"
                @click="exportTicket('csv')"
              >
                导出 CSV
              </el-button>
            </div>
          </div>

          <div v-loading="loadingDetail" class="thread ticket-thread">
            <EmptyState v-if="!loadingDetail && !messages.length" title="该工单还没有消息" />
            <div
              v-for="message in messages"
              :key="message.id"
              class="ticket-msg"
              :class="{ 'is-agent': message.senderRole === 'agent' }"
            >
              <div class="ticket-msg-head">
                <span>{{ message.senderRole === 'agent' ? '客服' : '访客' }} {{ message.senderName }}</span>
                <span>{{ formatTime(message.sentAt) }}</span>
              </div>
              <div class="ticket-msg-body">{{ message.content }}</div>
            </div>
          </div>
        </template>

        <div v-else class="empty-pane">
          <EmptyState title="选择一张工单" hint="左侧列表选择工单，查看聊天记录并导出。" />
        </div>
      </section>
    </main>
  </div>
</template>
