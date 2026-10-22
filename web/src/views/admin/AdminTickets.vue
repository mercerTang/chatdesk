<script setup>
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import EmptyState from '../../components/EmptyState.vue'
import { api } from '../../lib/api'
import { formatTime } from '../../lib/format'
import { toPlainText } from '../../lib/richText'

const agents = ref([])
const rows = ref([])
const loading = ref(false)
const page = ref(1)
const pageSize = 12

const filters = ref({ status: '', agentId: null, range: [] })

const drawerVisible = ref(false)
const detail = ref(null)
const detailLoading = ref(false)

const transferVisible = ref(false)
const transferTargetId = ref(null)
const transferNote = ref('')
const transferring = ref(false)

const statusOptions = [
  { label: '全部状态', value: '' },
  { label: '进行中', value: 'open' },
  { label: '已结束', value: 'closed' }
]

const paged = computed(() =>
  rows.value.slice((page.value - 1) * pageSize, page.value * pageSize)
)

// 目标客服：排除当前负责人；满员或已停用的不可选
const transferCandidates = computed(() =>
  agents.value.filter((agent) => agent.id !== detail.value?.ticket?.agentId)
)
const transferBlocked = (agent) =>
  !agent.isActive || agent.activeConversationCount >= agent.maxConcurrentConversations

const statusText = (status) => (status === 'open' ? '进行中' : '已结束')
const statusType = (status) => (status === 'open' ? 'primary' : 'info')

function buildParams() {
  const params = {}
  if (filters.value.status) {
    params.status = filters.value.status
  }
  if (filters.value.agentId) {
    params.agentId = filters.value.agentId
  }
  if (filters.value.range?.length === 2) {
    params.from = `${filters.value.range[0]}T00:00:00`
    params.to = `${filters.value.range[1]}T23:59:59`
  }
  return params
}

async function loadAgents() {
  try {
    agents.value = await api.adminAgents()
  } catch (error) {
    ElMessage.error(error.message)
  }
}

async function loadRows() {
  loading.value = true
  try {
    rows.value = await api.adminTickets(buildParams())
    page.value = 1
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    loading.value = false
  }
}

function resetFilters() {
  filters.value = { status: '', agentId: null, range: [] }
  loadRows()
}

async function openDetail(row) {
  drawerVisible.value = true
  detailLoading.value = true
  detail.value = null
  try {
    detail.value = await api.adminTicket(row.id)
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    detailLoading.value = false
  }
}

function openTransfer() {
  if (!detail.value || detail.value.ticket.status !== 'open') {
    return
  }
  transferTargetId.value = null
  transferNote.value = ''
  transferVisible.value = true
}

async function submitTransfer() {
  if (!detail.value || !transferTargetId.value) {
    ElMessage.warning('请选择目标客服')
    return
  }
  transferring.value = true
  try {
    await api.adminTransferTicket(detail.value.ticket.id, {
      agentId: transferTargetId.value,
      note: transferNote.value.trim() || null
    })
    transferVisible.value = false
    drawerVisible.value = false
    ElMessage.success('工单已强制转派，台账记录了原客服与本次操作的管理员')
    await Promise.all([loadRows(), loadAgents()])
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    transferring.value = false
  }
}

onMounted(async () => {
  await loadAgents()
  await loadRows()
})
</script>

<template>
  <section>
    <header class="admin-page-head">
      <div>
        <h2 class="admin-page-title">工单总览</h2>
        <p class="admin-page-sub">
          全部客服名下的工单，可查看任意工单的聊天记录与转派台账，并把进行中的工单强制转派。
        </p>
      </div>
      <div class="admin-page-actions">
        <el-button size="small" :loading="loading" @click="loadRows">刷新</el-button>
      </div>
    </header>

    <div class="admin-panel">
      <div class="admin-filters">
        <el-select v-model="filters.status" style="width: 150px" @change="loadRows">
          <el-option
            v-for="option in statusOptions"
            :key="option.value"
            :label="option.label"
            :value="option.value"
          />
        </el-select>

        <el-select
          v-model="filters.agentId"
          clearable
          filterable
          placeholder="全部客服"
          style="width: 200px"
          @change="loadRows"
        >
          <el-option
            v-for="agent in agents"
            :key="agent.id"
            :label="`${agent.displayName}（${agent.username}）${agent.isActive ? '' : ' · 已停用'}`"
            :value="agent.id"
          />
        </el-select>

        <el-date-picker
          v-model="filters.range"
          type="daterange"
          value-format="YYYY-MM-DD"
          start-placeholder="开始日期"
          end-placeholder="结束日期"
          style="width: 260px"
          @change="loadRows"
        />

        <el-button size="small" text @click="resetFilters">重置</el-button>
      </div>

      <el-table v-loading="loading" :data="paged" size="small" empty-text="没有符合条件的工单">
        <el-table-column prop="ticketNo" label="工单号" width="170" />
        <el-table-column prop="visitorName" label="访客" min-width="120" show-overflow-tooltip />
        <el-table-column prop="agentName" label="客服" min-width="120" show-overflow-tooltip />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="statusType(row.status)" effect="plain">
              {{ statusText(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="消息数" width="90">
          <template #default="{ row }">{{ row.messageCount }}</template>
        </el-table-column>
        <el-table-column label="最后消息" min-width="200" show-overflow-tooltip>
          <template #default="{ row }">{{ toPlainText(row.lastMessage) || '暂无消息' }}</template>
        </el-table-column>
        <el-table-column label="创建时间" width="140">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="90" fixed="right">
          <template #default="{ row }">
            <el-button text size="small" @click="openDetail(row)">详情</el-button>
          </template>
        </el-table-column>
      </el-table>

      <div v-if="rows.length > pageSize" class="admin-pager">
        <el-pagination
          v-model:current-page="page"
          :page-size="pageSize"
          layout="total, prev, pager, next"
          :total="rows.length"
          small
          background
        />
      </div>
    </div>

    <el-drawer v-model="drawerVisible" size="600px" :with-header="false">
      <div v-loading="detailLoading" class="admin-drawer">
        <template v-if="detail">
          <div class="admin-drawer-head">
            <div>
              <h3 class="admin-drawer-title">{{ detail.ticket.ticketNo }}</h3>
              <p class="admin-drawer-sub">
                {{ detail.ticket.visitorName }} → {{ detail.ticket.agentName }} ·
                {{ statusText(detail.ticket.status) }} · 创建于
                {{ formatTime(detail.ticket.createdAt) }}
              </p>
            </div>
            <div class="admin-drawer-actions">
              <el-button text size="small" @click="drawerVisible = false">关闭</el-button>
              <el-button
                type="primary"
                size="small"
                :disabled="detail.ticket.status !== 'open'"
                @click="openTransfer"
              >
                强制转派
              </el-button>
            </div>
          </div>

          <div class="admin-drawer-block">
            <div class="admin-drawer-label">转派台账</div>
            <div v-if="detail.transfers.length" class="admin-transfer-log">
              <div v-for="item in detail.transfers" :key="item.id">
                <div>
                  {{ formatTime(item.createdAt) }} · {{ item.fromAgentName }} → {{ item.toAgentName }}
                  <span v-if="item.operatorName">（管理员 {{ item.operatorName }} 强制转派）</span>
                </div>
                <div v-if="item.note" class="admin-transfer-note">备注：{{ item.note }}</div>
              </div>
            </div>
            <div v-else class="admin-transfer-log">还没有转派记录</div>
          </div>

          <div class="admin-drawer-block">
            <div class="admin-drawer-label">聊天记录（{{ detail.messages.length }} 条，保留最近 30 天）</div>
            <div v-if="detail.messages.length" class="admin-thread">
              <div
                v-for="message in detail.messages"
                :key="message.id"
                class="ticket-msg"
                :class="{ 'is-agent': message.senderRole === 'agent' }"
              >
                <div class="ticket-msg-head">
                  <span>
                    {{ message.senderRole === 'agent' ? '客服' : '访客' }} {{ message.senderName }}
                  </span>
                  <span>{{ formatTime(message.sentAt) }}</span>
                </div>
                <div class="ticket-msg-body">{{ message.content }}</div>
              </div>
            </div>
            <EmptyState v-else title="该工单还没有消息" />
          </div>
        </template>
      </div>
    </el-drawer>

    <el-dialog v-model="transferVisible" title="强制转派工单" width="460px">
      <p class="admin-page-sub" style="margin-top: 0">
        转派后原客服立即失去该会话的接收与回复权限，台账同时记录原客服与本次操作的管理员。
      </p>
      <div class="transfer-form">
        <div class="admin-drawer-label">目标客服</div>
        <el-select v-model="transferTargetId" filterable placeholder="选择接收的客服" style="width: 100%">
          <el-option
            v-for="agent in transferCandidates"
            :key="agent.id"
            :label="`${agent.displayName}（${agent.isOnline ? '在线' : '离线'} · ${agent.activeConversationCount}/${agent.maxConcurrentConversations}${agent.isActive ? '' : ' · 已停用'}）`"
            :value="agent.id"
            :disabled="transferBlocked(agent)"
          />
          <el-option
            v-if="!transferCandidates.length"
            label="没有可转派的客服"
            :value="0"
            disabled
          />
        </el-select>
        <div class="admin-drawer-label">备注（可选）</div>
        <el-input
          v-model="transferNote"
          type="textarea"
          :rows="2"
          maxlength="200"
          show-word-limit
          placeholder="说明调度原因，便于后续追溯"
        />
      </div>
      <template #footer>
        <el-button @click="transferVisible = false">取消</el-button>
        <el-button type="primary" :loading="transferring" @click="submitTransfer">确认转派</el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.admin-pager {
  display: flex;
  justify-content: flex-end;
  margin-top: 14px;
}

.transfer-form {
  display: grid;
  gap: 8px;
}

.admin-drawer-actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 6px;
}

.transfer-form .admin-drawer-label {
  margin-top: 6px;
}
</style>
