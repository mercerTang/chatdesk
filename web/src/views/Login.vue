<script setup>
import { onMounted, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { useRouter } from 'vue-router'
import { api } from '../lib/api'
import { auth, homePath, setSession } from '../lib/auth'
import BrandMark from '../components/BrandMark.vue'
import ThemeSwitch from '../components/ThemeSwitch.vue'

const router = useRouter()

const tab = ref('visitor')
const visitorMode = ref('login')
const submitting = ref(false)

const visitorFormRef = ref(null)
const agentFormRef = ref(null)

const visitorForm = reactive({ username: '', password: '', displayName: '' })
const agentForm = reactive({ username: '', password: '' })

const usernameRules = [
  { required: true, message: '请输入用户名', trigger: 'blur' },
  { min: 3, max: 32, message: '用户名长度 3-32 个字符', trigger: 'blur' },
  { pattern: /^\S+$/, message: '用户名不能包含空白字符', trigger: 'blur' }
]

const passwordRules = [
  { required: true, message: '请输入密码', trigger: 'blur' },
  { min: 6, max: 64, message: '密码长度 6-64 个字符', trigger: 'blur' }
]

const visitorRules = {
  username: usernameRules,
  password: passwordRules,
  displayName: [{ max: 64, message: '展示名不能超过 64 个字符', trigger: 'blur' }]
}

const agentRules = {
  username: usernameRules,
  password: passwordRules
}

onMounted(() => {
  if (auth.token && auth.user) {
    router.replace(homePath())
  }
})

watch(visitorMode, () => {
  visitorFormRef.value?.clearValidate()
})

async function submitVisitor() {
  const form = visitorFormRef.value
  if (!form) {
    return
  }
  const valid = await form.validate().catch(() => false)
  if (!valid) {
    return
  }

  submitting.value = true
  try {
    const result =
      visitorMode.value === 'register'
        ? await api.register({
            username: visitorForm.username,
            password: visitorForm.password,
            displayName: visitorForm.displayName
          })
        : await api.login({ username: visitorForm.username, password: visitorForm.password })

    setSession(result)
    ElMessage.success(visitorMode.value === 'register' ? '注册成功，已进入咨询' : '登录成功')
    router.replace(homePath(result.user))
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    submitting.value = false
  }
}

async function submitAgent() {
  const form = agentFormRef.value
  if (!form) {
    return
  }
  const valid = await form.validate().catch(() => false)
  if (!valid) {
    return
  }

  submitting.value = true
  try {
    const result = await api.login({ username: agentForm.username, password: agentForm.password })
    if (result.user.role !== 'agent') {
      ElMessage.error('该账号不是客服账号，请使用访客登录')
      return
    }
    setSession(result)
    ElMessage.success(`欢迎，${result.user.displayName}`)
    router.replace('/agent')
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="login-page">
    <ThemeSwitch class="login-theme-toggle" />
    <section class="login-brand">
      <div class="moon-stage" aria-hidden="true">
        <div class="moon-orbit orbit-two"></div>
        <div class="moon-orbit orbit-one"></div>
        <div class="moon"></div>
      </div>
      <BrandMark :size="42" />
      <h1>把问题说清楚，<br />会有人接住。</h1>
      <p class="brand-note">
        访客一次只和一位客服对话，客服可以同时接待多位访客。断线重连后，消息会按顺序补齐。
      </p>
    </section>

    <section class="login-panel">
      <div class="login-card">
        <h2 class="login-title">进入对话</h2>
        <p class="subtitle">登录或注册访客账号，也可以用客服账号进入工作台。</p>

        <el-tabs v-model="tab">
          <el-tab-pane label="访客" name="visitor">
            <div class="login-mode">
              <el-radio-group v-model="visitorMode" size="small">
                <el-radio-button value="login">登录</el-radio-button>
                <el-radio-button value="register">注册</el-radio-button>
              </el-radio-group>
            </div>

            <el-form
              ref="visitorFormRef"
              :model="visitorForm"
              :rules="visitorRules"
              label-position="top"
              @submit.prevent="submitVisitor"
            >
              <el-form-item label="用户名" prop="username">
                <el-input
                  v-model.trim="visitorForm.username"
                  placeholder="3-32 个字符，不含空白"
                  autocomplete="username"
                />
              </el-form-item>

              <el-form-item v-if="visitorMode === 'register'" label="展示名（可选）" prop="displayName">
                <el-input v-model.trim="visitorForm.displayName" maxlength="64" placeholder="默认用用户名" />
              </el-form-item>

              <el-form-item label="密码" prop="password">
                <el-input
                  v-model="visitorForm.password"
                  type="password"
                  show-password
                  placeholder="至少 6 位"
                  autocomplete="current-password"
                  @keyup.enter="submitVisitor"
                />
              </el-form-item>

              <el-button type="primary" class="submit" :loading="submitting" @click="submitVisitor">
                {{ visitorMode === 'register' ? '注册并进入' : '登录' }}
              </el-button>
            </el-form>
          </el-tab-pane>

          <el-tab-pane label="客服" name="agent">
            <el-form
              ref="agentFormRef"
              :model="agentForm"
              :rules="agentRules"
              label-position="top"
              @submit.prevent="submitAgent"
            >
              <el-form-item label="客服账号" prop="username">
                <el-input v-model.trim="agentForm.username" placeholder="agent1 / agent2 / agent3" />
              </el-form-item>

              <el-form-item label="密码" prop="password">
                <el-input
                  v-model="agentForm.password"
                  type="password"
                  show-password
                  placeholder="客服密码"
                  @keyup.enter="submitAgent"
                />
              </el-form-item>

              <el-button type="primary" class="submit" :loading="submitting" @click="submitAgent">
                客服登录
              </el-button>
            </el-form>
          </el-tab-pane>
        </el-tabs>

        <p class="tip">演示客服账号：agent1、agent2、agent3，密码 Agent@123456</p>
      </div>
    </section>
  </div>
</template>
