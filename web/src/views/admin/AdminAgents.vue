<script setup>
import { nextTick, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../../lib/api'
import { formatTime } from '../../lib/format'

const agents = ref([])
const loading = ref(false)
const busyId = ref(0)

const createVisible = ref(false)
const creating = ref(false)
const createFormRef = ref(null)
const createForm = reactive({
  username: '',
  displayName: '',
  password: '',
  maxConcurrentConversations: 5
})

const editVisible = ref(false)
const editing = ref(false)
const editFormRef = ref(null)
const editTarget = ref(null)
const editForm = reactive({ displayName: '', maxConcurrentConversations: 5 })

const resetVisible = ref(false)
const resetting = ref(false)
const resetFormRef = ref(null)
const resetTarget = ref(null)
const resetForm = reactive({ password: '' })

const usernameRules = [
  { required: true, message: '请输入登录名', trigger: 'blur' },
  { min: 3, max: 32, message: '登录名长度 3-32 个字符', trigger: 'blur' },
  { pattern: /^\S+$/, message: '登录名不能包含空白字符', trigger: 'blur' }
]

const displayNameRules = [
  { required: true, message: '请输入展示名', trigger: 'blur' },
  { max: 64, message: '展示名不能超过 64 个字符', trigger: 'blur' }
]

const passwordRules = [
  { required: true, message: '请输入密码', trigger: 'blur' },
  { min: 6, max: 64, message: '密码长度 6-64 个字符', trigger: 'blur' }
]

const limitRules = [
  { type: 'number', min: 1, max: 50, message: '并发上限 1-50', trigger: 'change' }
]

const createRules = {
  username: usernameRules,
  displayName: displayNameRules,
  password: passwordRules,
  maxConcurrentConversations: limitRules
}

const editRules = {
  displayName: displayNameRules,
  maxConcurrentConversations: limitRules
}

const resetRules = { password: passwordRules }

async function loadAgents() {
  loading.value = true
  try {
    agents.value = await api.adminAgents()
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    loading.value = false
  }
}

function replaceRow(updated) {
  agents.value = agents.value.map((item) => (item.id === updated.id ? updated : item))
}

function openCreate() {
  createForm.username = ''
  createForm.displayName = ''
  createForm.password = ''
  createForm.maxConcurrentConversations = 5
  createVisible.value = true
  nextTick(() => createFormRef.value?.clearValidate())
}

async function submitCreate() {
  const form = createFormRef.value
  if (!form) {
    return
  }
  const valid = await form.validate().catch(() => false)
  if (!valid) {
    return
  }

  creating.value = true
  try {
    const created = await api.adminCreateAgent({
      username: createForm.username,
      displayName: createForm.displayName || null,
      password: createForm.password,
      maxConcurrentConversations: createForm.maxConcurrentConversations
    })
    createVisible.value = false
    ElMessage.success(`已新增客服 ${created.displayName}，账号 ${created.username} 现在可以登录`)
    await loadAgents()
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    creating.value = false
  }
}

function openEdit(row) {
  editTarget.value = row
  editForm.displayName = row.displayName
  editForm.maxConcurrentConversations = row.maxConcurrentConversations
  editVisible.value = true
  nextTick(() => editFormRef.value?.clearValidate())
}

async function submitEdit() {
  const form = editFormRef.value
  if (!form || !editTarget.value) {
    return
  }
  const valid = await form.validate().catch(() => false)
  if (!valid) {
    return
  }

  editing.value = true
  try {
    const updated = await api.adminUpdateAgent(editTarget.value.id, {
      displayName: editForm.displayName,
      maxConcurrentConversations: editForm.maxConcurrentConversations
    })
    replaceRow(updated)
    editVisible.value = false
    ElMessage.success('客服资料已更新')
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    editing.value = false
  }
}

function openReset(row) {
  resetTarget.value = row
  resetForm.password = ''
  resetVisible.value = true
  nextTick(() => resetFormRef.value?.clearValidate())
}

async function submitReset() {
  const form = resetFormRef.value
  if (!form || !resetTarget.value) {
    return
  }
  const valid = await form.validate().catch(() => false)
  if (!valid) {
    return
  }

  resetting.value = true
  try {
    await api.adminUpdateAgent(resetTarget.value.id, { password: resetForm.password })
    resetVisible.value = false
    ElMessage.success(`已重置 ${resetTarget.value.displayName} 的密码，旧密码立即失效`)
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    resetting.value = false
  }
}

/** 启用 / 停用：停用只禁止新登录，并向在线连接推 SessionRevoked，不硬断进行中的会话。 */
async function toggleActive(row) {
  busyId.value = row.id
  try {
    const updated = await api.adminUpdateAgent(row.id, { isActive: !row.isActive })
    replaceRow(updated)
    ElMessage.success(
      updated.isActive
        ? `${updated.displayName} 已启用，可以重新登录`
        : `${updated.displayName} 已停用，在线连接会收到通知后登出`
    )
    return true
  } catch (error) {
    ElMessage.error(error.message)
    return false
  } finally {
    busyId.value = 0
  }
}

onMounted(loadAgents)
</script>

<template>
  <section>
    <header class="admin-page-head">
      <div>
        <h2 class="admin-page-title">客服管理</h2>
        <p class="admin-page-sub">
          新增客服、改展示名与并发上限、重置密码、启用停用。停用只禁止新登录，不打断进行中的会话。
        </p>
      </div>
      <div class="admin-page-actions">
        <el-button size="small" :loading="loading" @click="loadAgents">刷新</el-button>
        <el-button type="primary" size="small" @click="openCreate">新增客服</el-button>
      </div>
    </header>

    <div class="admin-panel">
      <el-table v-loading="loading" :data="agents" size="small" empty-text="还没有客服账号">
        <el-table-column prop="username" label="登录名" width="160" />
        <el-table-column prop="displayName" label="展示名" min-width="140" show-overflow-tooltip />
        <el-table-column label="在用状态" width="120">
          <template #default="{ row }">
            <el-switch
              :model-value="row.isActive"
              :loading="busyId === row.id"
              :before-change="() => toggleActive(row)"
              inline-prompt
              active-text="启用"
              inactive-text="停用"
            />
          </template>
        </el-table-column>
        <el-table-column label="在线" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.isOnline ? 'primary' : 'info'" effect="plain">
              {{ row.isOnline ? '在线' : '离线' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="活跃 / 上限" width="120">
          <template #default="{ row }">
            {{ row.activeConversationCount }} / {{ row.maxConcurrentConversations }}
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="150">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="170" fixed="right">
          <template #default="{ row }">
            <el-button text size="small" @click="openEdit(row)">编辑</el-button>
            <el-button text size="small" @click="openReset(row)">重置密码</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="createVisible" title="新增客服" width="440px">
      <el-form
        ref="createFormRef"
        :model="createForm"
        :rules="createRules"
        label-position="top"
        @submit.prevent="submitCreate"
      >
        <el-form-item label="登录名" prop="username">
          <el-input v-model.trim="createForm.username" placeholder="3-32 个字符，不含空白" />
        </el-form-item>
        <el-form-item label="展示名" prop="displayName">
          <el-input v-model.trim="createForm.displayName" maxlength="64" placeholder="访客看到的名字" />
        </el-form-item>
        <el-form-item label="初始密码" prop="password">
          <el-input v-model="createForm.password" type="password" show-password placeholder="至少 6 位" />
        </el-form-item>
        <el-form-item label="并发上限" prop="maxConcurrentConversations">
          <el-input-number
            v-model="createForm.maxConcurrentConversations"
            :min="1"
            :max="50"
            style="width: 100%"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createVisible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="submitCreate">创建</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="editVisible" title="编辑客服" width="440px">
      <el-form
        ref="editFormRef"
        :model="editForm"
        :rules="editRules"
        label-position="top"
        @submit.prevent="submitEdit"
      >
        <el-form-item label="展示名" prop="displayName">
          <el-input v-model.trim="editForm.displayName" maxlength="64" />
        </el-form-item>
        <el-form-item label="并发上限" prop="maxConcurrentConversations">
          <el-input-number
            v-model="editForm.maxConcurrentConversations"
            :min="1"
            :max="50"
            style="width: 100%"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="editVisible = false">取消</el-button>
        <el-button type="primary" :loading="editing" @click="submitEdit">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="resetVisible" title="重置密码" width="420px">
      <p class="admin-page-sub" style="margin-top: 0">
        给 {{ resetTarget?.displayName }}（{{ resetTarget?.username }}）设置新密码，旧密码立即失效。
      </p>
      <el-form
        ref="resetFormRef"
        :model="resetForm"
        :rules="resetRules"
        label-position="top"
        @submit.prevent="submitReset"
      >
        <el-form-item label="新密码" prop="password">
          <el-input
            v-model="resetForm.password"
            type="password"
            show-password
            placeholder="至少 6 位"
            @keyup.enter="submitReset"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="resetVisible = false">取消</el-button>
        <el-button type="primary" :loading="resetting" @click="submitReset">确认重置</el-button>
      </template>
    </el-dialog>
  </section>
</template>
