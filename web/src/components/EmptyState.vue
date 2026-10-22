<script setup>
defineProps({
  title: { type: String, default: '这里还没有内容' },
  hint: { type: String, default: '' }
})
</script>

<template>
  <div class="empty-state">
    <span class="empty-mark" aria-hidden="true">
      <img src="/logo.jpg" alt="" />
    </span>
    <p class="empty-title">{{ title }}</p>
    <p v-if="hint" class="empty-hint">{{ hint }}</p>
  </div>
</template>
