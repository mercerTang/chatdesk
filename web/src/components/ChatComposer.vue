<script setup>
import { ref } from 'vue'
import RichEditor from './RichEditor.vue'

const props = defineProps({
  disabled: { type: Boolean, default: false },
  placeholder: { type: String, default: '输入消息，支持加粗、列表、图片与附件' }
})

const emit = defineEmits(['send'])

const editor = ref(null)
const hasContent = ref(false)

function onChange(value) {
  hasContent.value = value
}

function submit() {
  if (props.disabled || !hasContent.value) {
    return
  }
  const markdown = editor.value?.getMarkdown() || ''
  if (!markdown) {
    return
  }
  editor.value.clear()
  emit('send', markdown)
}
</script>

<template>
  <div class="composer">
    <RichEditor
      ref="editor"
      :disabled="disabled"
      :placeholder="placeholder"
      @change="onChange"
      @submit="submit"
    />
    <div class="composer-actions">
      <span class="hint">Enter 发送，Shift + Enter 换行</span>
      <el-button type="primary" :disabled="disabled || !hasContent" @click="submit">发送</el-button>
    </div>
  </div>
</template>
