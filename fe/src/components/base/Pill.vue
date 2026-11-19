<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<script setup lang="ts">
defineOptions({ name: 'BasePill' })

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

type VariantType =
  | 'default'
  | 'primary'
  | 'success'
  | 'warning'
  | 'error'
  | 'info'
  | 'count'
  | 'subtle'

interface Props {
  variant?: VariantType
  size?: 'small' | 'medium' | 'large'
  outlined?: boolean
  /**
   * Render as a button so the pill can toggle the state it displays.
   * Defaults to false, keeping the plain span for read-only pills.
   */
  interactive?: boolean
  disabled?: boolean
}

withDefaults(defineProps<Props>(), {
  variant: 'default',
  size: 'medium',
  outlined: false,
  interactive: false,
  disabled: false,
})

// Declared explicitly rather than relying on attribute fallthrough: the template
// has v-if/v-else roots, so binding the listener here keeps the behaviour obvious
// and stops the same handler also arriving as a fallthrough attribute.
const emit = defineEmits<{ click: [MouseEvent] }>()
</script>

<template>
  <button
    v-if="interactive"
    type="button"
    :disabled="disabled"
    :class="['pill', `pill-${variant}`, `pill-${size}`, { outlined, interactive }]"
    @click="emit('click', $event)"
  >
    <slot />
  </button>
  <span v-else :class="['pill', `pill-${variant}`, `pill-${size}`, { outlined }]">
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

/* Interactive pills render as a button; reset the UA styles the span never had. */
button.pill {
  font: inherit;
  color: inherit;
}

.pill.interactive {
  cursor: pointer;
}

.pill.interactive:hover:not(:disabled) {
  filter: brightness(1.15);
}

.pill.interactive:disabled {
  cursor: default;
  opacity: 0.6;
}

.pill.interactive:focus-visible {
  outline: 2px solid currentColor;
  outline-offset: 2px;
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
