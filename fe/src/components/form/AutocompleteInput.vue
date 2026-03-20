<template>
  <div class="autocomplete-input-component">
    <input
      v-model="localValue"
      @input="onInput"
      @keypress.enter.prevent.stop="confirmHighlighted"
      @keydown.down.prevent="navigateSuggestion(1)"
      @keydown.up.prevent="navigateSuggestion(-1)"
      @keydown.escape="showSuggestions = false"
      @focus="showSuggestions = true"
      @blur="hideSuggestionsDelayed"
      v-bind="$attrs"
      type="text"
      autocomplete="off"
    />
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
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'

defineOptions({ name: 'AutocompleteInput', inheritAttrs: false })

const props = withDefaults(
  defineProps<{
    modelValue: string
    suggestions?: string[]
    maxSuggestions?: number
  }>(),
  {
    suggestions: () => [],
    maxSuggestions: 8,
  },
)

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const localValue = ref(props.modelValue)
const showSuggestions = ref(false)
const highlightedIndex = ref(-1)

// Sync prop → local (parent changes)
watch(() => props.modelValue, (v) => {
  if (v !== localValue.value) localValue.value = v
})
// Sync local → prop (user types)
watch(localValue, (v) => {
  if (v !== props.modelValue) emit('update:modelValue', v)
})

const filteredSuggestions = computed(() => {
  const query = localValue.value.trim().toLowerCase()
  if (!query) return []
  return props.suggestions
    .filter((s) => s.toLowerCase().includes(query) && s !== localValue.value)
    .slice(0, props.maxSuggestions)
})

function selectSuggestion(value: string) {
  localValue.value = value
  showSuggestions.value = false
  highlightedIndex.value = -1
}

function confirmHighlighted() {
  const highlighted = filteredSuggestions.value[highlightedIndex.value]
  if (highlighted) {
    selectSuggestion(highlighted)
  }
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
.autocomplete-input-component {
  position: relative;
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
