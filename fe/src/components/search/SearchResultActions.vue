<template>
  <div class="result-actions">
    <button
      :class="['btn', isAdded ? 'btn-success' : 'btn-primary']"
      :disabled="isAdded"
      @click="$emit('add')"
      :title="isAdded ? 'Already in library' : 'Add to library'"
    >
      <component :is="isAdded ? PhCheck : PhPlus" />
      {{ isAdded ? 'Added' : 'Add to Library' }}
    </button>
  </div>
</template>

<script setup lang="ts">
import { PhCheck, PhPlus } from '@phosphor-icons/vue'

interface Props {
  isAdded: boolean
}

withDefaults(defineProps<Props>(), {
  isAdded: false,
})

defineEmits<{
  add: []
}>()
</script>

<style scoped>
.result-actions {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  margin-top: auto;
}

.btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  padding: 0.5rem 1rem;
  border: none;
  border-radius: 0.375rem;
  font-size: 0.875rem;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.2s ease;
  white-space: nowrap;
}

.btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.btn-primary {
  background: var(--color-primary, #2196f3);
  color: white;
}

.btn-primary:not(:disabled):hover {
  background: var(--color-primary-dark, #1976d2);
}

.btn-primary:not(:disabled):active {
  transform: scale(0.98);
}

.btn-success {
  background: var(--color-success, #4caf50);
  color: white;
}

.btn-secondary {
  background: var(--color-surface, #e0e0e0);
  color: var(--color-text, #000);
}

.btn-secondary:not(:disabled):hover {
  background: var(--color-surface-hover, #d0d0d0);
}

.btn-secondary:not(:disabled):active {
  transform: scale(0.98);
}

.btn :deep(svg) {
  width: 1rem;
  height: 1rem;
  flex-shrink: 0;
}

@media (max-width: 640px) {
  .result-actions {
    flex-direction: column;
    width: 100%;
  }

  .btn {
    width: 100%;
  }
}
</style>
