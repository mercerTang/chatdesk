<script setup>
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { useRouter } from 'vue-router'
import BrandMark from '../components/BrandMark.vue'
import EmptyState from '../components/EmptyState.vue'
import RichText from '../components/RichText.vue'
import ThemeSwitch from '../components/ThemeSwitch.vue'
import { api } from '../lib/api'
import { auth, clearSession, updateUser } from '../lib/auth'
import { isExpired } from '../lib/localDb'
import { formatTime } from '../lib/format'
import { toPlainText } from '../lib/richText'

const router = useRouter()
const me = auth.user

const tickets = ref([])
const current = ref(null)
const messages = ref([])
const loading = ref(false)
const loadingDetail = ref(false)
const exporting = ref('')
const retentionDays = ref(30)
const transfers = ref([])
const agents = ref([])
const transferVisible = ref(false)
const transferTargetId = ref(null)
const transferNote = ref('')
const transferring = ref(false)

const transferCandidates = computed(() => agents.value.filter((agent) => agent.id !== me.id))
const transferFull = (agent) => agent.activeConversationCount >= agent.maxConcurrentConversations

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
    transfers.value = detail.transfers || []
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

async function openTransfer() {
  if (!current.value || current.value.status !== 'open') {
    return
  }
  transferTargetId.value = null
  transferNote.value = ''
  transferVisible.value = true
  try {
    agents.value = await api.agents()
  } catch (error) {
    ElMessage.error(error.message)
  }
}

async function submitTransfer() {
  if (!current.value || !transferTargetId.value) {
    ElMessage.warning('请选择目标客服')
    return
  }
  transferring.value = true
  try {
    await api.transferTicket(current.value.id, {
      agentId: transferTargetId.value,
      note: transferNote.value.trim() || null
    })
    transferVisible.value = false
    ElMessage.success('工单已转派，后续由目标客服接待')
    current.value = null
    messages.value = []
    transfers.value = []
    await loadTickets()
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    transferring.value = false
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
              <div class="item-sub">{{ toPlainText(ticket.lastMessage) || '暂无消息' }}</div>
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
              <el-button size="small" :disabled="current.status !== 'open'" @click="openTransfer">
                转派
              </el-button>
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

          <div v-if="transfers.length" class="transfer-log">
            <div v-for="item in transfers" :key="item.id" class="transfer-log-item">
              <span>{{ formatTime(item.createdAt) }}</span>
              <span>{{ item.fromAgentName }} → {{ item.toAgentName }}</span>
              <span v-if="item.note" class="transfer-log-note">{{ item.note }}</span>
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
              <div class="ticket-msg-body"><RichText :content="message.content" /></div>
            </div>
          </div>
        </template>

        <div v-else class="empty-pane">
          <EmptyState title="选择一张工单" hint="左侧列表选择工单，查看聊天记录并导出。" />
        </div>
      </section>
    </main>

    <el-dialog v-model="transferVisible" title="转派工单" width="440px">
      <div class="transfer-form">
        <div class="transfer-label">目标客服</div>
        <el-select v-model="transferTargetId" placeholder="选择另一位客服" style="width: 100%">
          <el-option
            v-for="agent in transferCandidates"
            :key="agent.id"
            :label="`${agent.displayName}（${agent.isOnline ? '在线' : '离线'} · ${agent.activeConversationCount}/${agent.maxConcurrentConversations}）`"
            :value="agent.id"
            :disabled="transferFull(agent)"
          />
          <el-option v-if="!transferCandidates.length" label="暂无可转派的客服" :value="0" disabled />
        </el-select>
        <div class="transfer-label">备注（可选）</div>
        <el-input
          v-model="transferNote"
          type="textarea"
          :rows="2"
          maxlength="200"
          show-word-limit
          placeholder="说明转派原因，便于后续追溯"
        />
      </div>
      <template #footer>
        <el-button @click="transferVisible = false">取消</el-button>
        <el-button type="primary" :loading="transferring" @click="submitTransfer">确认转派</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.transfer-log {
  display: grid;
  gap: 6px;
  padding: 10px 18px;
  border-bottom: 1px solid var(--el-border-color-lighter);
  background: var(--el-fill-color-lighter);
  font-size: 12px;
}

.transfer-log-item {
  display: flex;
  align-items: baseline;
  gap: 8px;
  color: var(--el-text-color-secondary);
}

.transfer-log-note {
  color: var(--el-text-color-regular);
}

.transfer-form {
  display: grid;
  gap: 8px;
}

.transfer-label {
  margin-top: 6px;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
</style>
