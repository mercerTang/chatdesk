<script setup>
defineProps({
  size: { type: Number, default: 24 },
  wordmark: { type: Boolean, default: true }
})
</script>

<template>
  <span class="brand-lockup">
    <span class="brand-logo-wrap" :style="{ width: `${size}px`, height: `${size}px` }">
      <img class="brand-logo" src="/logo.jpg" alt="在线客服" />
    </span>
    <span v-if="wordmark" class="brand-word">在线客服</span>
  </span>
</template>
