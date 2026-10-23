<script setup>
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../../lib/api'

/** 窗口档位：接口支持 1-90 天，仪表盘只给常看的三个。 */
const dayOptions = [
  { label: '近 7 天', value: 7 },
  { label: '近 14 天', value: 14 },
  { label: '近 30 天', value: 30 }
]

const days = ref(14)
const loading = ref(false)
const stats = ref(null)
/** 图表形态：柱状 / 折线。 */
const chartType = ref('bar')
/** 当前悬停的日期下标：三组序列联动高亮。 */
const hoverIndex = ref(-1)

const overview = computed(() => stats.value?.overview ?? null)
const agentRows = computed(() => stats.value?.agents ?? [])

/** 秒数转可读时长；没有样本时接口给 null。 */
function formatSeconds(value) {
  if (value === null || value === undefined) {
    return '—'
  }
  if (value < 60) {
    return `${Math.round(value)} 秒`
  }
  const minutes = value / 60
  return minutes < 60 ? `${minutes.toFixed(1)} 分钟` : `${(minutes / 60).toFixed(1)} 小时`
}

/** 日期标签只留「月-日」，接口给的是 yyyy-MM-dd。 */
function shortDate(value) {
  const text = String(value ?? '')
  return text.length >= 10 ? text.slice(5, 10) : text
}

function firstResponseHint(data) {
  if (!data) {
    return '访客首条消息 → 客服首条回复'
  }
  return data.firstResponseSamples > 0
    ? `${data.firstResponseSamples} 个会话有客服回复`
    : '窗口内还没有客服回复样本'
}

const overviewCards = computed(() => {
  const data = overview.value
  const windowLabel = `近 ${days.value} 天`
  return [
    {
      label: '新增会话',
      value: data ? data.conversationCount : '—',
      hint: `${windowLabel}按创建时间统计`
    },
    {
      label: '发送消息',
      value: data ? data.messageCount : '—',
      hint: `${windowLabel}按发送时间统计`
    },
    {
      label: '新建工单',
      value: data ? data.ticketCount : '—',
      hint: data ? `进行中 ${data.openTicketCount} 个（全量）` : `${windowLabel}按创建时间统计`
    },
    {
      label: '工单转派',
      value: data ? data.transferCount : '—',
      hint: `${windowLabel}的转派次数`
    },
    {
      label: '在线客服',
      value: data ? data.onlineAgentCount : '—',
      hint: '实时值，不受时间窗口限制'
    },
    {
      label: '平均首响',
      value: data ? formatSeconds(data.avgFirstResponseSeconds) : '—',
      hint: firstResponseHint(data)
    }
  ]
})

/** 三条趋势各自按自身峰值取高：消息量级远大于工单，共用一根坐标会把小值压平。 */
const trendSeries = computed(() => {
  const rows = stats.value?.daily ?? []
  const step = Math.max(1, Math.ceil(rows.length / 7))
  const build = (key, label) => {
    const max = rows.reduce((peak, row) => Math.max(peak, row[key]), 0)
    const points = rows.map((row, index) => {
      const x = rows.length <= 1 ? 0 : (index / (rows.length - 1)) * 100
      const y = max === 0 ? 100 : 100 - (row[key] / max) * 100
      return `${x.toFixed(2)},${y.toFixed(2)}`
    })
    return {
      key,
      label,
      max,
      total: rows.reduce((sum, row) => sum + row[key], 0),
      ticks: [max, Math.round(max / 2), 0],
      linePoints: points.join(' '),
      areaPoints: points.length ? `0,100 ${points.join(' ')} 100,100` : '',
      bars: rows.map((row, index) => {
        const value = row[key]
        const showLabel = index % step === 0 || index === rows.length - 1
        return {
          date: row.date,
          value,
          index,
          height: max === 0 || value === 0 ? '0%' : `${Math.max(4, Math.round((value / max) * 100))}%`,
          label: showLabel ? shortDate(row.date) : '',
          tip: `${shortDate(row.date)} · ${label} ${value}`
        }
      })
    }
  }
  return [build('conversations', '新会话'), build('messages', '消息'), build('tickets', '工单')]
})

async function loadStats() {
  loading.value = true
  try {
    stats.value = await api.adminStats(days.value)
  } catch (error) {
    ElMessage.error(error.message)
  } finally {
    loading.value = false
  }
}

onMounted(loadStats)
</script>

<template>
  <section v-loading="loading">
    <header class="admin-page-head">
      <div>
        <h2 class="admin-page-title">仪表盘</h2>
        <p class="admin-page-sub">
          近 {{ days }} 天按会话创建、消息发送与工单创建时间统计；在线客服与当前活跃是实时值。
        </p>
      </div>
      <div class="admin-page-actions">
        <el-select v-model="days" size="small" style="width: 118px" @change="loadStats">
          <el-option
            v-for="item in dayOptions"
            :key="item.value"
            :label="item.label"
            :value="item.value"
          />
        </el-select>
        <el-button size="small" :loading="loading" @click="loadStats">刷新</el-button>
      </div>
    </header>

    <div class="admin-cards">
      <div v-for="item in overviewCards" :key="item.label" class="admin-card">
        <div class="admin-card-label">{{ item.label }}</div>
        <div class="admin-card-value">{{ item.value }}</div>
        <div class="admin-card-hint">{{ item.hint }}</div>
      </div>
    </div>

    <div class="admin-panel stats-trend">
      <div class="admin-drawer-head">
        <div>
          <h3 class="admin-drawer-title">近 {{ days }} 天趋势</h3>
          <p class="admin-drawer-sub">按服务端本地日期切分，没有数据的日子也留一行</p>
        </div>
        <el-radio-group v-model="chartType" size="small">
          <el-radio-button label="bar">柱状</el-radio-button>
          <el-radio-button label="line">折线</el-radio-button>
        </el-radio-group>
      </div>

      <div v-for="series in trendSeries" :key="series.key" class="trend-row">
        <div class="trend-row-head">
          <span class="trend-label">{{ series.label }}</span>
          <span class="trend-total">合计 {{ series.total }} · 峰值 {{ series.max }}</span>
        </div>
        <div class="trend-chart">
          <div class="trend-scale">
            <span v-for="tick in series.ticks" :key="tick">{{ tick }}</span>
          </div>
          <div class="trend-plot">
            <span v-for="n in 3" :key="n" class="trend-grid-line" :style="{ top: `${(n - 1) * 50}%` }"></span>
            <svg
              v-if="chartType === 'line'"
              class="trend-line"
              viewBox="0 0 100 100"
              preserveAspectRatio="none"
              aria-hidden="true"
            >
              <polygon :points="series.areaPoints" class="trend-area" />
              <polyline
                :points="series.linePoints"
                class="trend-polyline"
                vector-effect="non-scaling-stroke"
              />
            </svg>
            <div class="trend-bars" :class="{ 'is-line': chartType === 'line' }">
              <div
                v-for="bar in series.bars"
                :key="bar.date"
                class="trend-slot"
                :class="{ 'is-hover': hoverIndex === bar.index }"
                @mouseenter="hoverIndex = bar.index"
                @mouseleave="hoverIndex = -1"
              >
                <div v-if="chartType === 'bar'" class="trend-bar" :style="{ height: bar.height }"></div>
                <span v-show="hoverIndex === bar.index" class="trend-tip">{{ bar.tip }}</span>
              </div>
            </div>
          </div>
        </div>
        <div class="trend-dates">
          <span class="trend-scale-spacer"></span>
          <div class="trend-date-row">
            <span v-for="bar in series.bars" :key="bar.date" class="trend-date">{{ bar.label }}</span>
          </div>
        </div>
      </div>
    </div>

    <div class="admin-panel">
      <div class="admin-drawer-head">
        <div>
          <h3 class="admin-drawer-title">客服排行</h3>
          <p class="admin-drawer-sub">接待与首响按窗口统计，当前活跃是实时负载</p>
        </div>
      </div>

      <el-table :data="agentRows" size="small" empty-text="还没有客服账号">
        <el-table-column prop="displayName" label="客服" min-width="140" show-overflow-tooltip />
        <el-table-column label="接待会话" width="110" align="right">
          <template #default="{ row }">{{ row.conversationCount }}</template>
        </el-table-column>
        <el-table-column label="发出消息" width="110" align="right">
          <template #default="{ row }">{{ row.messageCount }}</template>
        </el-table-column>
        <el-table-column label="平均首响" width="120" align="right">
          <template #default="{ row }">{{ formatSeconds(row.avgFirstResponseSeconds) }}</template>
        </el-table-column>
        <el-table-column label="首响样本" width="110" align="right">
          <template #default="{ row }">{{ row.firstResponseSamples }}</template>
        </el-table-column>
        <el-table-column label="当前活跃" width="110" align="right">
          <template #default="{ row }">{{ row.activeConversationCount }}</template>
        </el-table-column>
      </el-table>
    </div>
  </section>
</template>

<style scoped>
.stats-trend {
  margin-bottom: 20px;
}

.trend-row + .trend-row {
  margin-top: 16px;
}

.trend-row-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 6px;
}

.trend-label {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-muted);
}

.trend-total {
  font-size: 12px;
  color: var(--text-quiet);
  font-variant-numeric: tabular-nums;
}

.trend-bars {
  display: flex;
  align-items: flex-end;
  gap: 3px;
  height: 100%;
  position: relative;
  z-index: 1;
}

.trend-chart {
  display: flex;
  gap: 8px;
}

.trend-scale {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  width: 26px;
  height: 84px;
  font-size: 10px;
  line-height: 1;
  color: var(--text-quiet);
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.trend-plot {
  position: relative;
  flex: 1;
  height: 84px;
  border-bottom: 1px solid var(--hairline);
}

.trend-grid-line {
  position: absolute;
  left: 0;
  right: 0;
  border-top: 1px dashed rgba(100, 116, 139, 0.18);
  pointer-events: none;
}

.trend-slot {
  position: relative;
  flex: 1 1 0;
  min-width: 4px;
  height: 100%;
  display: flex;
  align-items: flex-end;
}

.trend-bar {
  width: 100%;
  min-height: 0;
  border-radius: 3px 3px 0 0;
  background: linear-gradient(180deg, rgba(37, 99, 235, 0.95), rgba(37, 99, 235, 0.55));
  transition: height 0.25s ease, filter 0.15s ease;
}

.trend-slot:hover .trend-bar {
  filter: brightness(1.12);
}

/* 三组序列联动：悬停某一天时，三行同一天的柱/点一起高亮 */
.trend-slot.is-hover::before {
  content: '';
  position: absolute;
  inset: 0;
  background: rgba(37, 99, 235, 0.07);
  border-radius: 3px;
}

.trend-line {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  pointer-events: none;
  overflow: visible;
}

.trend-polyline {
  fill: none;
  stroke: rgba(37, 99, 235, 0.9);
  stroke-width: 2;
  stroke-linejoin: round;
  stroke-linecap: round;
}

.trend-area {
  fill: rgba(37, 99, 235, 0.12);
}

.trend-tip {
  position: absolute;
  bottom: calc(100% + 6px);
  left: 50%;
  transform: translateX(-50%);
  padding: 3px 7px;
  border-radius: 6px;
  background: rgba(15, 23, 42, 0.92);
  color: #fff;
  font-size: 11px;
  line-height: 1.4;
  white-space: nowrap;
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.12s ease;
  z-index: 5;
}

.trend-slot:hover .trend-tip,
.trend-slot.is-hover .trend-tip {
  opacity: 1;
}

.trend-dates {
  display: flex;
  gap: 8px;
  margin-top: 4px;
}

.trend-scale-spacer {
  width: 26px;
  flex: none;
}

.trend-date-row {
  flex: 1;
  display: flex;
  gap: 3px;
}

.trend-date {
  flex: 1 1 0;
  min-width: 4px;
  font-size: 10px;
  line-height: 1.2;
  color: var(--text-quiet);
  text-align: center;
  white-space: nowrap;
}
</style>
