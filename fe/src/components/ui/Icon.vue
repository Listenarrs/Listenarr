<script setup lang="ts">
// Lightweight Icon wrapper: prefers an explicitly passed component (via `component` prop)
// and otherwise falls back to the legacy webfont CSS markup. This file intentionally
// does NOT import many icons — import icons per-view to allow better chunking.
import { computed } from 'vue'
import type { Component } from 'vue'

// Use a multi-word component name to satisfy the vue/multi-word-component-names rule
defineOptions({ name: 'AppIcon' })

const props = defineProps<{
  name?: string
  component?: Component
  size?: number | string
  weight?: 'thin' | 'light' | 'regular' | 'bold' | 'fill' | 'duotone'
  class?: string
}>()

const hasComponent = computed(() => !!props.component)
</script>

<template>
  <component
    v-if="hasComponent"
    :is="props.component"
    :size="props.size || 20"
    :weight="props.weight || 'regular'"
    :class="props.class"
  />
  <span v-else class="icon-placeholder" aria-hidden="true" />
</template>

<style scoped>
/* Intentionally small: styling comes from parent components or global CSS. */
</style>