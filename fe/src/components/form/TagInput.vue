<template>
  <div class="tags-container">
    <div class="tags-list">
      <span v-for="(item, index) in modelValue" :key="index" class="tag-item">
        {{ item }}
        <button type="button" class="tag-remove" @click="removeItem(index)" :title="`Remove ${item}`">
          <PhX :size="16" weight="bold" />
        </button>
      </span>
      <span v-if="modelValue.length === 0 && emptyText" class="tags-empty">{{ emptyText }}</span>
    </div>
    <div class="tag-input-group">
      <input
        type="text"
        v-model="inputValue"
        @keypress.enter.prevent.stop="addFromInput"
        @keydown.down.prevent="navigateSuggestion(1)"
        @keydown.up.prevent="navigateSuggestion(-1)"
        @keydown.escape="showSuggestions = false"
        @input="onInput"
        @focus="showSuggestions = true"
        @blur="hideSuggestionsDelayed"
        :placeholder="placeholder"
        class="tag-input"
        autocomplete="off"
      />
      <button
        type="button"
        @click="addFromInput"
        class="icon-btn btn-primary btn-add-tag"
        :disabled="!inputValue.trim()"
        :title="`Add ${label || 'item'}`"
        :aria-label="`Add ${label || 'item'}`"
      >
        <PhPlus :size="16" />
      </button>
      <ul v-if="showSuggestions && filteredSuggestions.length > 0" class="autocomplete-dropdown">
        <li
          v-for="(suggestion, idx) in filteredSuggestions"
          :key="suggestion"
          @mousedown.prevent="selectSuggestion(suggestion)"
          class="autocomplete-item"
          :class="{ 'is-highlighted': idx === highlightedIndex }"
        >
          {{ suggestion }}
        </li>
      </ul>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { PhX, PhPlus } from '@phosphor-icons/vue'

defineOptions({ name: 'TagInput' })

const props = withDefaults(
  defineProps<{
    modelValue: string[]
    suggestions?: string[]
    placeholder?: string
    emptyText?: string
    label?: string
    maxSuggestions?: number
  }>(),
  {
    suggestions: () => [],
    placeholder: 'Add item...',
    emptyText: '',
    label: '',
    maxSuggestions: 8,
  },
)

const emit = defineEmits<{
  'update:modelValue': [value: string[]]
}>()

const inputValue = ref('')
const showSuggestions = ref(false)
const highlightedIndex = ref(-1)

const filteredSuggestions = computed(() => {
  const query = inputValue.value.trim().toLowerCase()
  if (!query) return []
  return props.suggestions
    .filter((s) => s.toLowerCase().includes(query) && !props.modelValue.includes(s))
    .slice(0, props.maxSuggestions)
})

function addFromInput() {
  // If a suggestion is highlighted, select it instead of the raw input
  const highlighted = filteredSuggestions.value[highlightedIndex.value]
  if (highlighted) {
    selectSuggestion(highlighted)
    return
  }
  const value = inputValue.value.trim()
  if (value && !props.modelValue.includes(value)) {
    emit('update:modelValue', [...props.modelValue, value])
  }
  inputValue.value = ''
  showSuggestions.value = false
  highlightedIndex.value = -1
}

function removeItem(index: number) {
  const next = [...props.modelValue]
  next.splice(index, 1)
  emit('update:modelValue', next)
}

function selectSuggestion(value: string) {
  if (!props.modelValue.includes(value)) {
    emit('update:modelValue', [...props.modelValue, value])
  }
  inputValue.value = ''
  showSuggestions.value = false
  highlightedIndex.value = -1
}

function navigateSuggestion(direction: number) {
  if (!showSuggestions.value || filteredSuggestions.value.length === 0) return
  const len = filteredSuggestions.value.length
  highlightedIndex.value = (highlightedIndex.value + direction + len) % len
}

function onInput() {
  showSuggestions.value = true
  highlightedIndex.value = -1
}

function hideSuggestionsDelayed() {
  setTimeout(() => { showSuggestions.value = false }, 150)
}
</script>

<style scoped>
.tags-container {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.tags-list {
  display: flex;
  flex-wrap: wrap;
  gap: var(--spacing-sm);
  padding: 0.75rem;
  background-color: var(--bg-secondary);
  border: 1px solid var(--bg-surface);
  border-radius: var(--radius-md);
  min-height: 3rem;
  align-items: flex-start;
  align-content: flex-start;
}

.tag-item {
  display: inline-flex;
  align-items: center;
  gap: var(--spacing-sm);
  padding: 0.35rem 0.6rem;
  background-color: var(--bg-tertiary);
  color: var(--text-secondary);
  border-radius: var(--radius-md);
  font-size: 0.85rem;
  font-weight: 500;
  border: 1px solid var(--bg-surface);
  transition: all var(--transition-normal);
}

.tag-item:hover {
  background-color: var(--bg-surface);
  border-color: var(--brand-focus);
  color: var(--text-primary);
}

.tags-empty {
  color: #999;
  font-size: 0.875rem;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  padding: var(--spacing-sm);
}

.tag-remove {
  background: rgba(0, 0, 0, 0.2);
  border: none;
  color: var(--text-secondary);
  cursor: pointer;
  padding: var(--spacing-xs);
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: var(--radius-md);
  transition: all var(--transition-normal);
  flex-shrink: 0;
  margin-left: var(--spacing-xs);
}

.tag-remove:hover {
  background: var(--danger-600);
  color: var(--text-primary);
}

.tag-remove:active {
  background: rgba(255, 255, 255, 0.25);
}

.tag-input-group {
  display: flex;
  gap: var(--spacing-sm);
  position: relative;
}

.tag-input {
  flex: 1;
  padding: var(--control-padding);
  background-color: var(--bg-secondary);
  border: 1px solid var(--bg-surface);
  border-radius: var(--radius-md);
  color: var(--text-primary);
  font-size: 0.95rem;
  transition: all var(--transition-normal);
}

.tag-input:hover {
  border-color: var(--gray-600);
}

.tag-input:focus {
  outline: none;
  border-color: var(--brand-focus);
  background-color: var(--bg-tertiary);
  box-shadow: var(--focus-ring);
}

.tag-input::placeholder {
  color: var(--text-disabled);
}

.btn-add-tag {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: var(--spacing-sm);
  background-color: var(--brand-focus);
  color: var(--text-primary);
  border: none;
  border-radius: var(--btn-radius);
  cursor: pointer;
  transition: all var(--transition-normal);
  width: var(--control-height);
  height: var(--control-height);
}

.btn-add-tag:hover:not(:disabled) {
  background-color: var(--brand-700);
  transform: translateY(-1px);
}

.btn-add-tag:active:not(:disabled) {
  transform: translateY(0);
}

.btn-add-tag:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.autocomplete-dropdown {
  position: absolute;
  top: 100%;
  left: 0;
  right: 0;
  z-index: 50;
  background: var(--bg-tertiary);
  border: 1px solid var(--bg-surface);
  border-radius: var(--radius-md);
  max-height: 200px;
  overflow-y: auto;
  margin-top: 2px;
  padding: 0;
  list-style: none;
  box-shadow: var(--shadow-xl);
}

.autocomplete-item {
  padding: var(--spacing-sm) 0.75rem;
  cursor: pointer;
  font-size: 0.9rem;
  color: var(--text-secondary);
  transition: background var(--transition-fast);
}

.autocomplete-item:hover,
.autocomplete-item.is-highlighted {
  background: rgba(var(--brand-rgb), 0.15);
  color: var(--brand-400);
}
</style>
