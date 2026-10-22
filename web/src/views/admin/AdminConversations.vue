<script setup>
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../../lib/api'
import { formatTime } from '../../lib/format'
import { toPlainText } from '../../lib/richText'

const agents = ref([])
const rows = ref([])
const loading = ref(false)
const page = ref(1)
const pageSize = 12

const filters = ref({ status: '', agentId: null, range: [] })

const statusOptions = [
  { label: '全部状态', value: '' },
  { label: '进行中', value: 'active' },
  { label: '已结束', value: 'closed' }
]

const paged = computed(() =>
  rows.value.slice((page.value - 1) * pageSize, page.value * pageSize)
)

const statusText = (status) => (status === 'active' ? '进行中' : '已结束')
const statusType = (status) => (status === 'active' ? 'primary' : 'info')

function buildParams() {
  const params = {}
  if (filters.value.status) {
    params.status = filters.value.status
  }
  if (filters.value.agentId) {
    params.agentId = filters.value.agentId
  }
  if (filters.value.range?.length === 2) {
    // 日期范围按本地日期取整天边界
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
    rows.value = await api.adminConversations(buildParams())
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

onMounted(async () => {
  await loadAgents()
  await loadRows()
})
</script>

<template>
  <section>
    <header class="admin-page-head">
      <div>
        <h2 class="admin-page-title">会话总览</h2>
        <p class="admin-page-sub">全部客服名下的会话，按最后活跃时间倒序；时间筛选按会话创建时间。</p>
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

      <el-table v-loading="loading" :data="paged" size="small" empty-text="没有符合条件的会话">
        <el-table-column prop="id" label="会话" width="80" />
        <el-table-column prop="visitorName" label="访客" min-width="120" show-overflow-tooltip />
        <el-table-column prop="agentName" label="客服" min-width="120" show-overflow-tooltip />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="statusType(row.status)" effect="plain">
              {{ statusText(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="最后消息" min-width="220" show-overflow-tooltip>
          <template #default="{ row }">{{ toPlainText(row.lastMessage) || '暂无消息' }}</template>
        </el-table-column>
        <el-table-column label="消息数" width="90">
          <template #default="{ row }">{{ row.lastMessageSeq }}</template>
        </el-table-column>
        <el-table-column label="创建时间" width="140">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="最后活跃" width="140">
          <template #default="{ row }">{{ formatTime(row.lastActivityAt) }}</template>
        </el-table-column>
      </el-table>

      <div v-if="rows.length > pageSize" class="admin-pager">
        <el-pagination
          v-model:current-page="page"
          :page-size="pageSize"
          :total="rows.length"
          layout="total, prev, pager, next"
          small
          background
        />
      </div>
    </div>
  </section>
</template>

<style scoped>
.admin-pager {
  display: flex;
  justify-content: flex-end;
  margin-top: 14px;
}
</style>
