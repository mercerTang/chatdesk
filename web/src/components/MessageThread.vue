<script setup>
import { computed, nextTick, ref, watch } from 'vue'
import EmptyState from './EmptyState.vue'
import { formatTime } from '../lib/format'

const props = defineProps({
  items: { type: Array, default: () => [] }
})

const emit = defineEmits(['retry'])

const scroller = ref(null)

const STATE_LABEL = {
  sending: '发送中…',
  queued: '待发送（断线）',
  sent: '已发送',
  delivered: '已送达',
  read: '已读',
  failed: '发送失败，点击重试'
}

// 消息按 Seq 升序（旧消息在上、新消息在下）；未拿到 Seq 的本地消息排在末尾
const sorted = computed(() =>
  props.items.slice().sort((left, right) => {
    const leftSeq = left.seq || Number.MAX_SAFE_INTEGER
    const rightSeq = right.seq || Number.MAX_SAFE_INTEGER
    return leftSeq - rightSeq
  })
)

function stateLabel(state) {
  return STATE_LABEL[state] || ''
}

function onStateClick(item) {
  if (item.state === 'failed') {
    emit('retry', item)
  }
}

function scrollToBottom() {
  nextTick(() => {
    const element = scroller.value
    if (element) {
      element.scrollTop = element.scrollHeight
    }
  })
}

watch(() => props.items.length, scrollToBottom)
</script>

<template>
  <div ref="scroller" class="thread">
    <EmptyState
      v-if="!items.length"
      title="还没有消息"
      hint="发送第一条消息，对话从这里开始。"
    />
    <div
      v-for="item in sorted"
      :key="item.key"
      class="msg"
      :class="item.self ? 'is-self' : 'is-peer'"
    >
      <div class="bubble">{{ item.content }}</div>
      <div class="meta">
        <span>{{ formatTime(item.time) }}</span>
        <span
          v-if="item.self"
          class="state"
          :class="['state', item.state, { failed: item.state === 'failed' }]"
          @click="onStateClick(item)"
        >
          {{ stateLabel(item.state) }}
        </span>
      </div>
    </div>
  </div>
</template>
