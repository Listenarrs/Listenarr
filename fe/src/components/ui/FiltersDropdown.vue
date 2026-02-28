<template>
  <div ref="root" class="filters-dropdown">
    <button class="trigger" :class="{ active: active }" @click="toggle" :aria-expanded="open">
      <span class="trigger-label">Filters</span>
      <PhFunnel class="funnel-icon" />
    </button>

    <div v-if="open" class="dropdown">
      <div v-for="o in builtInOptions" :key="o.value" class="dropdown-item" :class="{ active: selectedBuiltIn === o.value }" @click="selectBuiltIn(o.value)">
        <div class="dropdown-item-main">
          <span>{{ o.label }}</span>
        </div>
        <div v-if="selectedBuiltIn === o.value" class="check">✓</div>
      </div>

      <div class="dropdown-divider"></div>

      <div v-if="customFilters.length === 0" class="dropdown-item">No custom filters</div>
      <div v-for="f in customFilters" :key="f.id" class="dropdown-item" :class="{ active: selectedCustom === f.id }">
        <div class="dropdown-item-main" @click="selectCustom(f.id)">{{ f.label }}</div>
        <div v-if="selectedCustom === f.id" class="check">✓</div>
        <div v-else class="dropdown-item-actions">
          <button class="icon-btn" @click.stop="emitEdit(f)">Edit</button>
          <button class="icon-btn delete" @click.stop="emitDelete(f)">Delete</button>
        </div>
      </div>

      <div class="dropdown-divider"></div>
      <div v-if="hasActiveFilter" class="dropdown-item reset" @click="resetFilter">Reset filter</div>
      <div class="dropdown-item create" @click="emitCreate">Create filter</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { PhFunnel } from '@phosphor-icons/vue'
interface CustomFilterRule {
  field: string
  operator: string
  value: string
}
interface CustomFilter {
  id: string
  label: string
  rules: CustomFilterRule[]
}

const props = withDefaults(
  defineProps<{
    customFilters: CustomFilter[]
    modelValue?: string | null
    active?: boolean
  }>(),
  {
    modelValue: null,
    active: false,
  },
)

const emit = defineEmits<{
  (e: 'update:modelValue', v: string | null): void
  (e: 'create'): void
  (e: 'edit', filter: CustomFilter): void
  (e: 'delete', filter: CustomFilter): void
}>()

const open = ref(false)
const root = ref<HTMLElement | null>(null)

const builtInOptions = [
  { value: 'monitored', label: 'Monitored Only' },
  { value: 'unmonitored', label: 'Unmonitored Only' },
  { value: 'missing', label: 'Missing' },
  { value: 'recent', label: 'Recently Added' },
]

const customFilters = computed(() => props.customFilters || [])

const selectedBuiltIn = computed(() => {
  const v = props.modelValue
  if (!v) return null
  if (builtInOptions.some((o) => o.value === v)) return v
  return null
})

const selectedCustom = computed(() => {
  const v = props.modelValue
  if (!v) return null
  if (customFilters.value.some((f: CustomFilter) => f.id === v)) return v
  return null
})

const hasActiveFilter = computed(() => !!props.modelValue)

function toggle() {
  open.value = !open.value
}

function close() {
  open.value = false
}

function selectBuiltIn(val: string) {
  emit('update:modelValue', val === selectedBuiltIn.value ? null : val)
  close()
}

function selectCustom(id: string) {
  emit('update:modelValue', id === selectedCustom.value ? null : id)
  close()
}

function resetFilter() {
  emit('update:modelValue', null)
  close()
}

function emitCreate() {
  emit('create')
  close()
}

function emitEdit(f: CustomFilter) {
  emit('edit', f)
  close()
}

function emitDelete(f: CustomFilter) {
  emit('delete', f)
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
.filters-dropdown {
  position: relative;
  display: inline-block;
}
.trigger {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  color: #e6eef8;
  cursor: pointer;
  font-size: 12px;
}
.trigger.active {
  background-color: var(--brand-500);
  border-color: var(--brand-500);
  color: #fff;
}
.dropdown {
  position: absolute;
  top: calc(100% + 6px);
  left: auto;
  right: 0;
  min-width: 220px;
  background: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.6);
  z-index: 1100;
  max-height: 60vh;
  overflow-y: auto;
}

.dropdown-item {
  padding: 0.75rem 1rem;
  cursor: pointer;
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 12px;
  transition: background-color 0.15s;
}
.dropdown-item:hover {
  background-color: rgba(255, 255, 255, 0.18);
  color: #fff;
}
.dropdown-divider {
  height: 1px;
  background: rgba(255, 255, 255, 0.04);
  margin: 6px 0;
}
.dropdown-item.create {
  font-weight: 500;
  color: #fff;
}
.dropdown-item.reset {
  font-weight: 500;
  color: #fff;
}
.check {
  color: #4dabf7;
}
.dropdown-item-main {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1;
}
.dropdown-item-actions {
  display: flex;
  gap: 6px;
  margin-left: 8px;
}

/* Mobile-friendly toolbar: hide text, show only icons on screens 1024px and below */
@media (max-width: 1024px) {
  .dropdown {
    min-width: 180px;
    max-width: calc(100vw - 16px);
  }
  
  .trigger {
    padding: 8px 6px;
    min-width: 36px;
    justify-content: center;
    gap: 0;
  }
  /* Hide text label on mobile, show only funnel icon */
  .trigger-label {
    display: none;
  }
  .funnel-icon {
    width: 18px;
    height: 18px;
  }
}

.funnel-icon {
  width: 16px;
  height: 16px;
  flex-shrink: 0;
}

.dropdown-item.active {
  background-color: rgba(33, 150, 243, 0.1);
  color: #fff;
}
</style>
