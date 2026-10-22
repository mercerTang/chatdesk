<script setup>
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import Quill from 'quill'
import 'quill/dist/quill.snow.css'
import TurndownService from 'turndown'
import { gfm } from 'turndown-plugin-gfm'
import { api } from '../lib/api'

const props = defineProps({
  disabled: { type: Boolean, default: false },
  placeholder: { type: String, default: '输入消息…' }
})

const emit = defineEmits(['change', 'submit'])

const host = ref(null)
const toolbar = ref(null)
const imageInput = ref(null)
const fileInput = ref(null)
const uploading = ref(false)

let quill = null

const turndown = new TurndownService({
  headingStyle: 'atx',
  codeBlockStyle: 'fenced',
  bulletListMarker: '-',
  emDelimiter: '*'
})
turndown.use(gfm)

const MAX_IMAGE_BYTES = 10 * 1024 * 1024
const MAX_FILE_BYTES = 10 * 1024 * 1024

function notifyChange() {
  // 只有图片、没有文字也算有内容
  const hasText = quill ? quill.getText().trim().length > 0 : false
  const hasImage = quill ? !!quill.root.querySelector('img') : false
  emit('change', hasText || hasImage)
}

async function uploadAndInsert(file, mode) {
  if (!file) {
    return
  }
  const limit = mode === 'image' ? MAX_IMAGE_BYTES : MAX_FILE_BYTES
  if (file.size > limit) {
    ElMessage.error('文件不能超过 10 MB')
    return
  }
  uploading.value = true
  try {
    const asset = await api.uploadFile(file)
    const range = quill.getSelection(true) || { index: quill.getLength() }
    if (mode === 'image') {
      quill.insertEmbed(range.index, 'image', asset.url, 'user')
      quill.setSelection(range.index + 1, 0, 'silent')
    } else {
      quill.insertText(range.index, asset.name, { link: asset.url }, 'user')
      quill.setSelection(range.index + asset.name.length, 0, 'silent')
    }
    quill.focus()
  } catch (error) {
    ElMessage.error(error.message || '上传失败')
  } finally {
    uploading.value = false
  }
}

function pickImage() {
  imageInput.value?.click()
}

function pickFile() {
  fileInput.value?.click()
}

async function onImageChosen(event) {
  const file = event.target.files?.[0]
  await uploadAndInsert(file, 'image')
  event.target.value = ''
}

async function onFileChosen(event) {
  const file = event.target.files?.[0]
  await uploadAndInsert(file, 'file')
  event.target.value = ''
}

async function onPaste(event) {
  const files = [...(event.clipboardData?.files || [])]
  const image = files.find((item) => item.type.startsWith('image/'))
  if (image) {
    // 捕获阶段拦下：不能让 Quill 把图片转成 data URL 直接内联
    event.preventDefault()
    event.stopPropagation()
    await uploadAndInsert(image, 'image')
  }
}

async function onDrop(event) {
  const files = [...(event.dataTransfer?.files || [])]
  const image = files.find((item) => item.type.startsWith('image/'))
  if (image) {
    event.preventDefault()
    event.stopPropagation()
    await uploadAndInsert(image, 'image')
  }
}

function onKeydown(event) {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing && event.keyCode !== 229) {
    event.preventDefault()
    emit('submit')
  }
}

onMounted(() => {
  quill = new Quill(host.value, {
    theme: 'snow',
    placeholder: props.placeholder,
    modules: {
      toolbar: {
        container: toolbar.value,
        handlers: {
          image: pickImage,
          attachment: pickFile
        }
      }
    }
  })

  quill.on('text-change', notifyChange)
  quill.root.addEventListener('keydown', onKeydown)

  // Quill 默认给「行内代码」和「代码块」同一个 </> 图标，看起来像重复按钮；代码块换成 { } 以示区分
  const codeBlockButton = toolbar.value?.querySelector('.ql-code-block')
  if (codeBlockButton) {
    codeBlockButton.innerHTML = '<span class="tool-glyph">{ }</span>'
  }
  // 捕获阶段优先处理：Quill 默认会把粘贴的图片内联成 data URL
  quill.root.addEventListener('paste', onPaste, true)
  quill.root.addEventListener('drop', onDrop, true)
})

onBeforeUnmount(() => {
  if (quill) {
    quill.root.removeEventListener('keydown', onKeydown)
    quill.root.removeEventListener('paste', onPaste, true)
    quill.root.removeEventListener('drop', onDrop, true)
  }
  quill = null
})

/** 编辑器内容 → Markdown 源文（消息以 Markdown 存储）。 */
function getMarkdown() {
  if (!quill) {
    return ''
  }
  const html = quill.root.innerHTML
  return turndown.turndown(html).trim()
}

function clear() {
  if (!quill) {
    return
  }
  quill.setText('')
  quill.focus()
  notifyChange()
}

defineExpose({ getMarkdown, clear })
</script>

<template>
  <div class="rich-editor" :class="{ 'is-disabled': disabled }">
    <div ref="toolbar" class="editor-toolbar">
      <button class="ql-bold" type="button" aria-label="粗体" data-tip="粗体 Ctrl+B"></button>
      <button class="ql-italic" type="button" aria-label="斜体" data-tip="斜体 Ctrl+I"></button>
      <button class="ql-strike" type="button" aria-label="删除线" data-tip="删除线"></button>
      <button class="ql-code" type="button" aria-label="行内代码" data-tip="行内代码：选中一段文字"></button>
      <button class="ql-code-block" type="button" aria-label="代码块" data-tip="代码块：整段多行"></button>
      <button class="ql-blockquote" type="button" aria-label="引用" data-tip="引用"></button>
      <button class="ql-list" value="ordered" type="button" aria-label="有序列表" data-tip="有序列表"></button>
      <button class="ql-list" value="bullet" type="button" aria-label="无序列表" data-tip="无序列表"></button>
      <button class="ql-link" type="button" aria-label="链接" data-tip="插入链接"></button>
      <button class="ql-image" type="button" aria-label="插入图片" data-tip="插入图片（也可直接粘贴或拖入）"></button>
      <button class="ql-attachment" type="button" aria-label="上传附件" data-tip="上传附件（≤10 MB）">📎</button>
    </div>

    <div ref="host" class="editor-host"></div>

    <div v-if="uploading" class="editor-tip">正在上传…</div>

    <input ref="imageInput" type="file" accept="image/png,image/jpeg,image/gif,image/webp" hidden @change="onImageChosen" />
    <input
      ref="fileInput"
      type="file"
      accept=".png,.jpg,.jpeg,.gif,.webp,.pdf,.txt,.csv,.zip,.doc,.docx,.xls,.xlsx"
      hidden
      @change="onFileChosen"
    />
  </div>
</template>

<style scoped>
.rich-editor {
  border: 1px solid var(--hairline);
  border-radius: 10px;
  background: var(--surface-2);
  overflow: hidden;
}

.editor-toolbar {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 4px 6px;
  border-bottom: 1px solid var(--hairline);
}

.editor-toolbar :deep(button) {
  width: 28px;
  height: 26px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-regular);
  cursor: pointer;
}

.editor-toolbar :deep(button:hover) {
  background: rgba(37, 99, 235, 0.1);
  color: #2563eb;
}

.editor-toolbar :deep(button.ql-active) {
  background: rgba(37, 99, 235, 0.16);
  color: #2563eb;
}

/* 悬停提示：按钮下方浮出说明，替代浏览器原生的 title 提示 */
.editor-toolbar :deep(button) {
  position: relative;
}

.editor-toolbar :deep(button[data-tip]::after) {
  content: attr(data-tip);
  position: absolute;
  top: calc(100% + 8px);
  left: 50%;
  transform: translateX(-50%) translateY(-2px);
  padding: 4px 8px;
  border-radius: 6px;
  background: rgba(15, 23, 42, 0.92);
  color: #fff;
  font-size: 12px;
  font-weight: 400;
  line-height: 1.4;
  white-space: nowrap;
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.12s ease, transform 0.12s ease;
  z-index: 30;
}

.editor-toolbar :deep(button[data-tip]:hover::after) {
  opacity: 1;
  transform: translateX(-50%) translateY(0);
}

/* 首尾两个按钮的提示贴边，避免被容器圆角裁掉 */
.editor-toolbar :deep(button:first-child::after) {
  left: 0;
  transform: none;
}

.editor-toolbar :deep(button:first-child:hover::after) {
  transform: none;
}

.editor-toolbar :deep(button:last-child::after) {
  left: auto;
  right: 0;
  transform: none;
}

.editor-toolbar :deep(button:last-child:hover::after) {
  transform: none;
}

.editor-toolbar :deep(.tool-glyph) {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 11px;
  letter-spacing: -0.5px;
  white-space: nowrap;
}

.editor-toolbar :deep(button:has(.tool-glyph)) {
  width: auto;
  min-width: 30px;
  padding: 0 4px;
}

.editor-host {
  min-height: 96px;
  max-height: 220px;
  overflow-y: auto;
}

.editor-host :deep(.ql-editor) {
  min-height: 96px;
  padding: 10px 12px;
  font-size: 14px;
  line-height: 1.6;
  color: var(--text-regular);
}

.editor-host :deep(.ql-editor.ql-blank::before) {
  color: var(--text-muted, #94a3b8);
  font-style: normal;
}

.editor-host :deep(.ql-editor img) {
  max-width: 100%;
  border-radius: 8px;
}

.editor-tip {
  padding: 2px 12px 6px;
  font-size: 12px;
  color: var(--text-muted, #94a3b8);
}

.is-disabled {
  opacity: 0.6;
  pointer-events: none;
}
</style>
