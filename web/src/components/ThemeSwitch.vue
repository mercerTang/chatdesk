<script setup>
import { computed } from 'vue'
import { setTheme, theme } from '../lib/theme'

const light = computed({
  get: () => theme.current === 'light',
  set: (value) => setTheme(value ? 'light' : 'dark')
})
</script>

<template>
  <span class="theme-switch" title="切换亮色主题">
    <span>亮色</span>
    <el-switch v-model="light" size="small" />
  </span>
</template>
