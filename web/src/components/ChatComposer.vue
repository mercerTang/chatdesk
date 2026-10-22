<script setup>
import { ref } from 'vue'

const props = defineProps({
  disabled: { type: Boolean, default: false },
  placeholder: { type: String, default: '输入消息，Enter 发送，Shift+Enter 换行' }
})

const emit = defineEmits(['send'])

const text = ref('')

function submit() {
  const content = text.value.trim()
  if (!content || props.disabled) {
    return
  }
  text.value = ''
  emit('send', content)
}

/** 中文输入法组合中按 Enter 是选词，不能当成发送。 */
function onEnter(event) {
  if (event.isComposing || event.keyCode === 229) {
    return
  }
  event.preventDefault()
  submit()
}
</script>

<template>
  <div class="composer">
    <el-input
      v-model="text"
      type="textarea"
      :rows="3"
      resize="none"
      maxlength="2000"
      show-word-limit
      :disabled="disabled"
      :placeholder="placeholder"
      @keydown.enter.exact="onEnter"
    />
    <div class="composer-actions">
      <span class="hint">Enter 发送，Shift + Enter 换行</span>
      <el-button type="primary" :disabled="disabled || !text.trim()" @click="submit">发送</el-button>
    </div>
  </div>
</template>
