<script setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { useRouter } from 'vue-router'
import ChatComposer from '../components/ChatComposer.vue'
import EmptyState from '../components/EmptyState.vue'
import MessageThread from '../components/MessageThread.vue'
import BrandMark from '../components/BrandMark.vue'
import ThemeSwitch from '../components/ThemeSwitch.vue'
import { api } from '../lib/api'
import { auth, clearSession, updateUser } from '../lib/auth'
import { createChatStore } from '../lib/chatStore'
import { flashTitle } from '../lib/notify'

const router = useRouter()
const me = auth.user

const mine = ref([])
const current = ref(null)
const starting = ref(false)
const loadError = ref('')

const LAST_KEY = 'chat.lastVisitorConversation'

const store = createChatStore({
  selfId: me.id,
  hooks: {
    onMessage: (message) => {
      if (message.senderId !== me.id) {
        flashTitle('新消息')
      }
    },
    onConversationClosed: (payload) => {
      if (current.value?.id === payload.conversationId) {
        current.value = { ...current.value, status: 'closed' }
      }
      refreshMine()
    },
    onConversationTransferred: (payload) => {
      if (current.value?.id === payload.conversationId) {
        current.value = {
          ...current.value,
          agentId: payload.agentId,
          agentName: payload.agentName
        }
        ElMessage.info(`客服已变更为 ${payload.agentName}，会话继续进行`)
      }
      mine.value = mine.value.map((conversation) =>
        conversation.id === payload.conversationId
          ? { ...conversation, agentId: payload.agentId, agentName: payload.agentName }
          : conversation
      )
    },
    onRestored: () => refreshMine(),
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

async function refreshMine() {
  try {
    mine.value = await api.myConversations()
  } catch {
    /* 列表刷新失败不阻塞对话 */
  }
}

async function enterConversation(conversation) {
  current.value = conversation
  localStorage.setItem(LAST_KEY, String(conversation.id))
  await store.open(conversation)
}

/** 没有进行中的会话时直接匹配一位在线客服；匹配不到时由空态提示重试。 */
async function startConversation() {
  if (starting.value) {
    return
  }
  starting.value = true
  loadError.value = ''
  try {
    const conversation = await api.createConversation(null)
    await enterConversation(conversation)
    await refreshMine()
  } catch (error) {
    loadError.value = error.message
    ElMessage.warning(error.message)
  } finally {
    starting.value = false
  }
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
    await refreshMine()
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

  await refreshMine()
  await store.connect()

  const preferredId = Number(localStorage.getItem(LAST_KEY) || 0)
  const remembered = mine.value.find((conversation) => conversation.id === preferredId)
  const active = mine.value.find((conversation) => conversation.status === 'active')
  const target = remembered?.status === 'active' ? remembered : active

  if (target) {
    await enterConversation(target)
  } else {
    await startConversation()
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
        <el-tag :type="connectionType" effect="plain" size="small">{{ connectionLabel }}</el-tag>
        <span class="user">{{ me.displayName }}（{{ me.username }}）</span>
        <el-button text size="small" @click="logout">退出登录</el-button>
      </div>
    </header>

    <main class="layout">
      <section class="chat-pane">
        <template v-if="current">
          <div class="chat-head">
            <div>
              <div class="chat-title">{{ current.agentName }}</div>
              <div class="chat-sub">{{ closed ? '会话已结束' : '正在与客服对话' }}</div>
            </div>
            <div class="chat-head-actions">
              <el-button v-if="!closed" size="small" @click="closeConversation">结束会话</el-button>
              <el-button v-else size="small" type="primary" :loading="starting" @click="startConversation">
                重新开始咨询
              </el-button>
            </div>
          </div>

          <MessageThread :items="items" @retry="retry" />
          <ChatComposer
            :disabled="closed"
            :placeholder="closed ? '会话已结束' : '输入消息，Enter 发送，Shift+Enter 换行'"
            @send="send"
          />
        </template>

        <div v-else class="empty-pane">
          <div class="empty-block">
            <EmptyState
              title="还没有开始对话"
              :hint="loadError || '点下面的按钮，系统会为你匹配一位在线客服。'"
            />
            <el-button type="primary" :loading="starting" @click="startConversation">开始咨询</el-button>
          </div>
        </div>
      </section>
    </main>
  </div>
</template>
