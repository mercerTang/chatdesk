<script setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage, ElNotification } from 'element-plus'
import { useRouter } from 'vue-router'
import ChatComposer from '../components/ChatComposer.vue'
import EmptyState from '../components/EmptyState.vue'
import MessageThread from '../components/MessageThread.vue'
import BrandMark from '../components/BrandMark.vue'
import ThemeSwitch from '../components/ThemeSwitch.vue'
import { api } from '../lib/api'
import { auth, clearSession, updateUser } from '../lib/auth'
import { createChatStore } from '../lib/chatStore'
import { notifyDesktop } from '../lib/desktop'
import { formatTime, initialOf } from '../lib/format'
import { toPlainText } from '../lib/richText'
import { flashTitle } from '../lib/notify'

const router = useRouter()
const me = auth.user

const conversations = ref([])
const current = ref(null)
const myLimit = ref(5)
const loadingList = ref(false)

const CURRENT_KEY = 'chat.agentConversationId'

/** 新消息提醒：桌面端走系统通知（点通知聚焦窗口），浏览器退回应用内通知。 */
function notifyNewMessage(title, message) {
  if (notifyDesktop({ title, body: message })) {
    return
  }
  ElNotification({
    title,
    message,
    position: 'bottom-right',
    duration: 6000,
    customClass: 'chat-notice'
  })
}

const store = createChatStore({
  selfId: me.id,
  hooks: {
    onMessage: (message) => {
      const conversation = conversations.value.find((entry) => entry.id === message.conversationId)
      if (!conversation) {
        return
      }
      conversation.lastMessage = message.content
      conversation.lastMessageAt = message.sentAt
      conversation.lastMessageSeq = message.seq

      if (current.value?.id === message.conversationId) {
        conversation.unreadCount = 0
      } else if (message.senderId !== me.id) {
        conversation.unreadCount = (conversation.unreadCount || 0) + 1
        flashTitle('新消息')
        const preview =
          message.content.length > 40 ? `${message.content.slice(0, 40)}…` : message.content
        notifyNewMessage(conversation.visitorName || '新消息', preview)
      }

      // 左侧列表按最后消息时间倒序，最新会话浮到最上面
      conversations.value = sortByActivity(conversations.value)
    },
    onConversationCreated: (conversation) => {
      const existing = conversations.value.findIndex(
        (entry) => entry.visitorId === conversation.visitorId
      )
      if (existing >= 0) {
        conversations.value.splice(existing, 1, conversation)
      } else {
        conversations.value = [conversation, ...conversations.value]
      }
      conversations.value = sortByActivity(conversations.value)
      ElMessage.info(`新会话：${conversation.visitorName}`)
      // 服务端只在连接建立时把客服并入已有会话组，新会话要主动加入
      store.track(conversation)
    },
    onConversationClosed: (payload) => {
      const conversation = conversations.value.find((entry) => entry.id === payload.conversationId)
      if (conversation) {
        conversation.status = 'closed'
      }
      if (current.value?.id === payload.conversationId) {
        current.value = { ...current.value, status: 'closed' }
      }
      store.setStatus(payload.conversationId, 'closed')
    },
    onConversationTransferred: (payload) => {
      // 自己名下的会话被转出：从列表移除，正在查看的会话退回空态
      const index = conversations.value.findIndex((entry) => entry.id === payload.conversationId)
      if (index >= 0) {
        conversations.value.splice(index, 1)
      }
      if (current.value?.id === payload.conversationId) {
        current.value = null
      }
      store.forget(payload.conversationId)
      ElMessage.info(`会话已转派给 ${payload.agentName}`)
      notifyDesktop({ title: '会话已转派', body: `已转派给 ${payload.agentName}` })
    },
    onConversationAssigned: (conversation) => {
      // 转派转入：按访客 upsert（同一访客只保留一行）并接管实时接收
      const existing = conversations.value.findIndex(
        (entry) => entry.visitorId === conversation.visitorId
      )
      if (existing >= 0) {
        conversations.value.splice(existing, 1, conversation)
      } else {
        conversations.value = [conversation, ...conversations.value]
      }
      conversations.value = sortByActivity(conversations.value)
      ElMessage.success(`新转入会话：${conversation.visitorName}`)
      notifyDesktop({ title: '新转入会话', body: conversation.visitorName })
      store.track(conversation)
    },
    onRestored: () => refreshConversations(),
    onSessionRevoked: () => {
      // 账号被管理员停用：服务端不断连，只通知前端登出（进行中的会话不被打断）
      clearSession()
      ElMessage.error('账号已被停用，已退回登录页，请联系管理员')
      notifyDesktop({ title: '账号已被停用', body: '已退回登录页，请联系管理员' })
      router.replace('/login')
    },
    onError: (error) => ElMessage.warning(error.message)
  }
})

const connectionState = computed(() => store.state.connection)
const connectionLabel = computed(
  () =>
    ({
      connected: '已连接',
      connecting: '连接中…',
      reconnecting: '重连中…',
      offline: '已断开，自动重试中'
    })[connectionState.value] || '未连接'
)
const connectionType = computed(() => {
  if (connectionState.value === 'connected') {
    return 'success'
  }
  if (connectionState.value === 'offline') {
    return 'info'
  }
  return 'warning'
})

const items = computed(() => store.items(current.value?.id))
const closed = computed(() => Boolean(current.value) && current.value.status !== 'active')
const activeCount = computed(
  () => conversations.value.filter((conversation) => conversation.status === 'active').length
)
const loadLabel = computed(
  () => `进行中 ${activeCount.value} 个会话，同时接待上限 ${myLimit.value} 位`
)

async function refreshConversations() {
  loadingList.value = true
  try {
    conversations.value = sortByActivity(dedupeByVisitor(await api.myConversations()))
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    loadingList.value = false
  }
}

function activityTime(conversation) {
  return new Date(conversation.lastMessageAt || conversation.lastActivityAt || 0).getTime()
}

/** 左侧列表按最后消息时间倒序（最新会话在最上面）。 */
function sortByActivity(list) {
  return list.slice().sort((left, right) => activityTime(right) - activityTime(left))
}

/** 同一访客只保留一行（服务端已按进行中优先排序，这里做前端冗余防御）。 */
function dedupeByVisitor(list) {
  const seen = new Set()
  return list.filter((conversation) => {
    if (seen.has(conversation.visitorId)) {
      return false
    }
    seen.add(conversation.visitorId)
    return true
  })
}

async function loadMyLimit() {
  try {
    const agents = await api.agents()
    const self = agents.find((agent) => agent.id === me.id)
    if (self) {
      myLimit.value = self.maxConcurrentConversations
    }
  } catch {
    /* 上限展示失败时保留默认值，不影响接待 */
  }
}

async function enterConversation(conversation) {
  current.value = conversation
  localStorage.setItem(CURRENT_KEY, String(conversation.id))
  await store.open(conversation)
  conversation.unreadCount = 0
}

async function send(text) {
  if (!current.value) {
    return
  }
  try {
    await store.send(current.value.id, text)
  } catch (error) {
    ElMessage.warning(error.message)
  }
}

function retry(item) {
  if (current.value) {
    store.retry(current.value.id, item)
  }
}

async function closeConversation() {
  if (!current.value) {
    return
  }
  try {
    const updated = await api.closeConversation(current.value.id)
    current.value = updated
    store.setStatus(updated.id, updated.status)
    await refreshConversations()
  } catch (error) {
    ElMessage.error(error.message)
  }
}

function logout() {
  store.dispose()
  clearSession()
  router.replace('/login')
}

onMounted(async () => {
  try {
    updateUser(await api.me())
  } catch {
    /* 资料同步失败不影响对话 */
  }

  await Promise.all([refreshConversations(), loadMyLimit()])
  await store.connect()

  const preferredId = Number(localStorage.getItem(CURRENT_KEY) || 0)
  const remembered = conversations.value.find((conversation) => conversation.id === preferredId)
  const active = conversations.value.find((conversation) => conversation.status === 'active')
  const target = remembered?.status === 'active' ? remembered : active || remembered
  if (target) {
    await enterConversation(target)
  }
})

onBeforeUnmount(() => store.dispose())
</script>

<template>
  <div class="page">
    <header class="topbar">
      <BrandMark />
      <div class="topbar-right">
        <ThemeSwitch />
        <el-button text size="small" @click="router.push('/tickets')">工单记录</el-button>
        <el-tag :type="connectionType" effect="plain" size="small">{{ connectionLabel }}</el-tag>
        <span class="user">{{ me.displayName }}（{{ me.username }}）</span>
        <el-button text size="small" @click="logout">退出登录</el-button>
      </div>
    </header>

    <main class="layout">
      <aside class="sidebar">
        <div class="sidebar-head">
          <span>会话列表</span>
          <el-button text size="small" :loading="loadingList" @click="refreshConversations">刷新</el-button>
        </div>

        <div class="sidebar-note">{{ loadLabel }}</div>

        <div v-loading="loadingList" class="list">
          <div v-if="!conversations.length" class="list-empty">
            <EmptyState title="还没有访客会话" hint="访客发起咨询后会出现在这里。" />
          </div>

          <button
            v-for="conversation in conversations"
            :key="conversation.id"
            type="button"
            class="item"
            :class="{ 'is-active': current && current.id === conversation.id }"
            @click="enterConversation(conversation)"
          >
            <el-avatar :size="38">{{ initialOf(conversation.visitorName) }}</el-avatar>
            <div class="item-body">
              <div class="item-title">
                <span>{{ conversation.visitorName }}</span>
                <el-tag v-if="conversation.status !== 'active'" size="small" type="info" effect="plain">
                  已关闭
                </el-tag>
              </div>
              <div class="item-sub">{{ toPlainText(conversation.lastMessage) || '暂无消息' }}</div>
            </div>
            <div class="item-side">
              <span>{{ formatTime(conversation.lastMessageAt) }}</span>
              <el-tag
                v-if="conversation.unreadCount > 0 && current && current.id !== conversation.id"
                size="small"
                type="primary"
                effect="dark"
                round
              >
                {{ conversation.unreadCount }}
              </el-tag>
            </div>
          </button>
        </div>
      </aside>

      <section class="chat-pane">
        <template v-if="current">
          <div class="chat-head">
            <div>
              <div class="chat-title">{{ current.visitorName }}</div>
              <div class="chat-sub">
                {{ closed ? '会话已关闭' : `对话进行中 · 访客 ID ${current.visitorId}` }}
              </div>
            </div>
            <div class="chat-head-actions">
              <el-button v-if="!closed" size="small" @click="closeConversation">结束会话</el-button>
            </div>
          </div>

          <MessageThread :items="items" @retry="retry" />
          <ChatComposer
            :disabled="closed"
            :placeholder="closed ? '会话已关闭' : '输入回复，Enter 发送，Shift+Enter 换行'"
            @send="send"
          />
        </template>

        <div v-else class="empty-pane">
          <EmptyState title="选择一位访客" hint="从左侧列表打开会话，查看并回复。" />
        </div>
      </section>
    </main>
  </div>
</template>
