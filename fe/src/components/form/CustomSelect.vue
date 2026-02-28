<template>
  <div class="custom-select" ref="root">
    <button
      type="button"
      class="select-trigger"
      :class="{ active: active }"
      @click="toggle"
      :aria-expanded="open ? 'true' : 'false'"
      :aria-haspopup="'listbox'"
    >
      <span class="label">{{ selectedLabel }}</span>
      <component :is="sortIcon" class="sort-icon" />
    </button>

    <ul v-if="open" class="select-dropdown" role="listbox">
      <li
        v-for="opt in options"
        :key="opt.value"
        class="select-item"
        :class="{ active: opt.value === currentValue }"
        role="option"
        @click="select(opt.value)"
      >
        <span class="item-label">{{ opt.label }}</span>
        <component v-if="opt.value === currentValue" :is="directionIndicator" class="direction-indicator" />
      </li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { PhSortAscending, PhSortDescending, PhArrowUp, PhArrowDown } from '@phosphor-icons/vue'

const props = withDefaults(
  defineProps<{
    modelValue?: string | null
    options?: Array<{ value: string; label: string; icon?: any }>
    sortOrder?: 'asc' | 'desc'
    currentValue?: string | null
    active?: boolean
  }>(),
  { modelValue: null, options: () => [], sortOrder: 'asc', currentValue: null, active: false },
)

const emit = defineEmits<{ (e: 'update:modelValue', v: string | null): void }>()

const open = ref(false)
const root = ref<HTMLElement | null>(null)

const options = computed(() => props.options || [])

const selectedLabel = computed(() => {
  const found = options.value.find((o) => o.value === props.modelValue)
  return found ? found.label : options.value[0]?.label ?? ''
})

const sortIcon = computed(() => {
  return props.sortOrder === 'asc' ? PhSortAscending : PhSortDescending
})

const directionIndicator = computed(() => {
  return props.sortOrder === 'asc' ? PhArrowDown : PhArrowUp
})

function toggle() {
  open.value = !open.value
}

function close() {
  open.value = false
}

function select(v: string) {
  emit('update:modelValue', v)
  close()
}

function handleClickOutside(e: MouseEvent) {
  if (!root.value) return
  if (!root.value.contains(e.target as Node)) close()
}

onMounted(() => document.addEventListener('click', handleClickOutside))
onUnmounted(() => document.removeEventListener('click', handleClickOutside))
</script>

<style scoped>
.custom-select { position: relative; display: inline-block }
.select-trigger {
  background: #2a2a2a;
  color: #e6eef8;
  border: 1px solid rgba(255,255,255,0.06);
  padding: 8px 10px;
  border-radius: 6px;
  display: inline-flex;
  align-items: center;
  gap: 8px;
}
.select-trigger .label {
  font-size: 12px;
}
.select-trigger.active {
  background-color: var(--brand-500);
  border-color: var(--brand-500);
  color: #fff;
}
.select-dropdown {
  position: absolute;
  top: calc(100% + 6px);
  left: auto;
  right: 0;
  min-width: 180px;
  background: #2a2a2a;
  border: 1px solid rgba(255,255,255,0.06);
  border-radius: 6px;
  box-shadow: 0 8px 24px rgba(0,0,0,0.6);
  z-index: 1100;
  list-style: none;
  padding: 6px 0;
  margin: 0;
  max-height: 60vh;
  overflow-y: auto;
}

@media (max-width: 1024px) {
  .select-dropdown {
    /* Align to right on tablet/mobile to prevent overflow */
    min-width: 140px;
    max-width: calc(100vw - 16px);
  }
  
  .select-trigger {
    padding: 8px 6px;
    gap: 4px;
  }
  
  .select-trigger .label {
    display: none;
  }
  
}

.select-item {
  padding: 0.75rem 1rem;
  cursor: pointer;
  color: #ddd;
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 12px;
  transition: background-color 0.15s;
}
.select-item:hover {
  background-color: rgba(255, 255, 255, 0.18);
  color: #fff;
}
.select-item.active {
  background-color: rgba(33, 150, 243, 0.1);
  color: #fff;
}

.sort-icon {
  width: 16px;
  height: 16px;
  flex-shrink: 0;
}

.direction-indicator {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: #2196F3;
  margin-left: 8px;
}
</style>