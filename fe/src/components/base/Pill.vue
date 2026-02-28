<script setup lang="ts">
import type { Component } from 'vue'

/**
 * Pill Component
 *
 * A versatile badge/pill component for displaying metadata, counts, and features.
 * Different from StatusBadge which is for status indicators.
 *
 * Usage Examples:
 *
 * 1. Simple text pill:
 *    <Pill>SSL</Pill>
 *
 * 2. With icon:
 *    <Pill>
 *      <PhLock />
 *      SSL Enabled
 *    </Pill>
 *
 * 3. Count badge:
 *    <Pill variant="count">5</Pill>
 *
 * 4. Colored variants:
 *    <Pill variant="primary">Monitored</Pill>
 *    <Pill variant="success">RSS</Pill>
 *    <Pill variant="warning">Beta</Pill>
 *    <Pill variant="error">Issues</Pill>
 */

type VariantType = 'default' | 'primary' | 'success' | 'warning' | 'error' | 'info' | 'count' | 'subtle'

interface Props {
  variant?: VariantType
  size?: 'small' | 'medium' | 'large'
  outlined?: boolean
}

withDefaults(defineProps<Props>(), {
  variant: 'default',
  size: 'medium',
  outlined: false,
})
</script>

<template>
  <span :class="['pill', `pill-${variant}`, `pill-${size}`, { outlined }]">
    <slot />
  </span>
</template>

<style scoped>
.pill {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  border-radius: 6px;
  font-weight: 500;
  white-space: nowrap;
  transition: all 0.2s;
  border: 1px solid transparent;
}

/* Size variants */
.pill-small {
  padding: 0.2rem 0.5rem;
  font-size: 0.7rem;
}

.pill-medium {
  padding: 0.375rem 0.75rem;
  font-size: 0.75rem;
}

.pill-large {
  padding: 0.5rem 1rem;
  font-size: 0.85rem;
}

/* Default variant */
.pill-default {
  background-color: rgba(156, 163, 175, 0.15);
  color: #d1d5db;
  border-color: rgba(156, 163, 175, 0.3);
}

/* Primary variant (brand color) */
.pill-primary {
  background-color: rgba(33, 150, 243, 0.15);
  color: #2196f3;
  border-color: rgba(33, 150, 243, 0.3);
}

/* Success variant */
.pill-success {
  background-color: rgba(46, 204, 113, 0.15);
  color: #2ecc71;
  border-color: rgba(46, 204, 113, 0.3);
}

/* Warning variant */
.pill-warning {
  background-color: rgba(243, 156, 18, 0.15);
  color: #f39c12;
  border-color: rgba(243, 156, 18, 0.3);
}

/* Error variant */
.pill-error {
  background-color: rgba(231, 76, 60, 0.15);
  color: #e74c3c;
  border-color: rgba(231, 76, 60, 0.3);
}

/* Info variant */
.pill-info {
  background-color: rgba(155, 89, 182, 0.15);
  color: #9b59b6;
  border-color: rgba(155, 89, 182, 0.3);
}

/* Count variant (for notification counts) */
.pill-count {
  background-color: var(--brand-500);
  color: white;
  border-color: var(--brand-500);
  font-weight: 500;
  min-width: 1.5rem;
  justify-content: center;
}

/* Subtle variant (low contrast) */
.pill-subtle {
  background-color: rgba(255, 255, 255, 0.05);
  color: #aaa;
  border-color: rgba(255, 255, 255, 0.1);
}

/* Outlined modifier */
.pill.outlined {
  background-color: transparent !important;
}

/* Icon sizing */
.pill :deep(svg) {
  width: 0.875em;
  height: 0.875em;
  flex-shrink: 0;
}
</style>
