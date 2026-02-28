<template>
  <div class="root-folder-select">
    <label v-if="!hideLabel" class="form-label">Root Folder</label>

    <div v-if="store.loading" class="loading-row">
      <PhSpinner class="ph-spin" />
      <span>Loading root folders...</span>
    </div>

    <div v-else>
      <div :class="['root-select-content', props.inline ? 'inline' : '']">
        <select class="form-select" :value="selectValue" @change="onChange">
          <option :value="NULL_VALUE">Use default</option>
          <option v-for="f in store.folders" :key="f.id" :value="String(f.id)">
            {{ f.name }} — {{ f.path }}
          </option>
          <option :value="CUSTOM_VALUE">Custom path</option>
        </select>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { PhSpinner, PhInfo } from '@phosphor-icons/vue'

const NULL_VALUE = '__null__'
const CUSTOM_VALUE = '__custom__'

const props = withDefaults(
  defineProps<{ rootId?: number | null; customPath?: string | null; hideLabel?: boolean; hideBrowse?: boolean; autoFocusCustom?: boolean; inline?: boolean; externalCustom?: boolean }>(),
  { rootId: null, customPath: null, hideLabel: false, hideBrowse: false, autoFocusCustom: false, inline: false, externalCustom: false },
)



const emit = defineEmits(['update:rootId', 'update:customPath', 'open-browser', 'custom-selected'])

const store = useRootFoldersStore()

onMounted(() => {
  // Ensure root folders are loaded for selection
  void store.load()
})
const isCustom = computed(() => {
  return (props.customPath && props.customPath.length > 0) || selectValueComputed.value === CUSTOM_VALUE
})

const selectValueComputed = computed(() => {
  if (props.customPath && props.customPath.length > 0) return CUSTOM_VALUE
  // Treat explicit 0 as Custom sentinel
  if (props.rootId === 0) return CUSTOM_VALUE
  if (props.rootId == null) return NULL_VALUE
  return String(props.rootId)
})

// When the selection switches to Custom, optionally autofocus the custom input
watch(
  () => selectValueComputed.value,
  (v) => {
    if (v === CUSTOM_VALUE) {
      // Notify parent that custom selection was chosen (useful when the parent
      // renders the custom input externally) so it can autofocus the external input.
      emit('custom-selected')
    }
  },
) 

const selectValue = selectValueComputed

function onChange(e: Event) {
  const v = (e.target as HTMLSelectElement).value
  if (v === CUSTOM_VALUE) {
    // Switch to custom path mode. Use 0 to explicitly denote 'custom' in parent.
    emit('update:rootId', 0)
    emit('update:customPath', props.customPath || '')
    // Notify parent so it can take any action (e.g., autofocus an external input)
    emit('custom-selected')
  } else if (v === NULL_VALUE) {
    emit('update:rootId', null)
    emit('update:customPath', null)
  } else {
    const id = Number(v)
    if (!Number.isNaN(id)) {
      emit('update:rootId', id)
      emit('update:customPath', null)
    }
  }
}
</script>

<style scoped>
.root-folder-select {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}
.root-select-content.inline { display:flex; gap:0.5rem; align-items:center; flex-wrap:wrap; width:100% }



.recent-paths { width:100% }



.form-select {
  padding: 0.75rem 1rem;
  height: 40px;
  box-sizing: border-box;
  background-color: #1a1a1a; /* match input background */
  border: 1px solid #333;
  border-radius: 6px;
  color: white;
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
}

.form-select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.2);
}  

.form-input {
  padding: 0.6rem 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #333;
  color: white;
  border-radius: 6px;
}

.loading-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #adb5bd;
}
</style>