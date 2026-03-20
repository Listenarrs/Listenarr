<template>
  <span class="rename-path-diff">
    <span class="path-old">{{ oldPath }}</span>
    <PhArrowRight class="arrow-icon" />
    <span class="path-new">{{ newPath }}</span>
  </span>
</template>

<script setup lang="ts">
import { PhArrowRight } from '@phosphor-icons/vue'

defineProps<{
  oldPath?: string
  newPath?: string
}>()
</script>

<style scoped>
.rename-path-diff {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
  font-size: 0.85rem;
  font-family: 'Courier New', monospace;
}

.path-old {
  color: var(--text-danger, #ef5350);
  word-break: break-all;
}

.path-new {
  color: var(--text-success, #66bb6a);
  word-break: break-all;
}

.arrow-icon {
  flex-shrink: 0;
  color: var(--text-muted, #adb5bd);
  width: 16px;
  height: 16px;
}
</style>
