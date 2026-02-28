<script setup lang="ts">
import type { Component } from 'vue'

/**
 * StatusCard Component
 *
 * A semantic card component for displaying status information with:
 * - Flex layout with header and details sections
 * - Icon + title in header with optional status badge
 * - Details section for displaying status-specific content
 *
 * Usage:
 * <StatusCard title="API Status" icon="PhCheckCircle">
 *   <template #header-badge>
 *     <span class="status-badge success">Active</span>
 *   </template>
 *   <div class="detail-row">
 *     <span class="label">Version:</span>
 *     <span class="value">1.0.0</span>
 *   </div>
 * </StatusCard>
 */

withDefaults(
  defineProps<{
    title: string
    icon?: Component
  }>(),
  {
    icon: undefined,
  }
)
</script>

<template>
  <div class="status-card">
    <div class="status-header">
      <div class="card-title">
        <component v-if="icon" :is="icon" />
        <h3>{{ title }}</h3>
      </div>
      <slot name="header-badge" />
    </div>
    <div class="status-details">
      <slot />
    </div>
  </div>
</template>

<style scoped>
.status-card {
  background: #232323;
  border: 1px solid #333;
  border-radius: 8px;
  padding: 1.5rem;
  box-shadow: 0 6px 18px rgba(0,0,0,0.25);
  transition: all 0.2s;
}

.status-card:hover {
  border-color: #444;
  transform: translateY(-2px);
}

.status-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1.25rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid #333;
}

.card-title {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.card-title :deep(svg) {
  font-size: 1.5rem;
  color: var(--brand-focus);
}

.card-title h3 {
  margin: 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
}
</style>
