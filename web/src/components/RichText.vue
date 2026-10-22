<script setup>
import { computed } from 'vue'
import { renderRich } from '../lib/richText'

const props = defineProps({
  content: { type: String, default: '' }
})

const html = computed(() => renderRich(props.content))
</script>

<template>
  <div class="rich-text" v-html="html" />
</template>

<style scoped>
.rich-text {
  word-break: break-word;
  white-space: normal;
}

.rich-text :deep(p) {
  margin: 0 0 6px;
}

.rich-text :deep(p:last-child) {
  margin-bottom: 0;
}

.rich-text :deep(pre) {
  margin: 6px 0;
  padding: 8px 10px;
  border-radius: 8px;
  background: rgba(15, 23, 42, 0.06);
  overflow-x: auto;
  white-space: pre-wrap;
}

.rich-text :deep(code) {
  padding: 1px 4px;
  border-radius: 4px;
  background: rgba(15, 23, 42, 0.08);
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.92em;
}

.rich-text :deep(pre code) {
  padding: 0;
  background: none;
}

.rich-text :deep(blockquote) {
  margin: 6px 0;
  padding: 2px 10px;
  border-left: 3px solid rgba(59, 130, 246, 0.45);
  color: inherit;
  opacity: 0.9;
}

.rich-text :deep(ul),
.rich-text :deep(ol) {
  margin: 4px 0;
  padding-left: 20px;
}

.rich-text :deep(a) {
  color: #2563eb;
  text-decoration: underline;
}

.rich-text :deep(img) {
  display: block;
  max-width: min(100%, 320px);
  max-height: 320px;
  margin: 6px 0;
  border-radius: 8px;
  object-fit: contain;
}

.rich-text :deep(hr) {
  border: none;
  border-top: 1px solid rgba(15, 23, 42, 0.12);
  margin: 8px 0;
}

:global(.dark) .rich-text :deep(pre),
:global(.dark) .rich-text :deep(code) {
  background: rgba(255, 255, 255, 0.08);
}

:global(.dark) .rich-text :deep(a) {
  color: #7cb2ff;
}
</style>
