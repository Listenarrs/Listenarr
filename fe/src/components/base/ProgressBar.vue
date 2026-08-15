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
import { computed } from 'vue'

/**
 * ProgressBar Component
 *
 * A reusable progress bar component supporting:
 * - Simple progress display with percentage
 * - Progress with size information
 * - Progress with stats (speed, ETA, etc.)
 * - Different visual styles (gradient, solid colors)
 *
 * Usage Examples:
 *
 * 1. Simple:
 *    <ProgressBar :value="progress" />
 *
 * 2. With size info:
 *    <ProgressBar :value="progress" :downloaded="downloadedSize" :total="totalSize" />
 *
 * 3. Storage/system style (gradient):
 *    <ProgressBar :value="percentage" variant="storage" />
 *
 * 4. Download style (with speed/ETA):
 *    <ProgressBar :value="progress" :downloaded="size" :total="total" variant="download" />
 *
 * 5. Activity style (with animation):
 *    <ProgressBar :value="progress" variant="activity" />
 */

interface Props {
  value: number // Progress percentage (0-100)
  downloaded?: number // Downloaded size in bytes
  total?: number // Total size in bytes
  variant?: 'default' | 'storage' | 'download' | 'activity' // Visual style
  height?: 'small' | 'medium' | 'large' // Bar height
  showPercentage?: boolean // Show percentage text
  showSize?: boolean // Show size info
  label?: string // Optional label above bar
  animating?: boolean // For activity-style animation
  indeterminate?: boolean // Show activity without implying measurable completion
}

const props = withDefaults(defineProps<Props>(), {
  downloaded: undefined,
  total: undefined,
  variant: 'default',
  height: 'medium',
  showPercentage: true,
  showSize: false,
  label: undefined,
  animating: false,
  indeterminate: false,
})

// Format bytes to human readable size
const formatSize = (bytes: number): string => {
  if (bytes === 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const size = Math.abs(bytes)
  let unitIndex = 0
  let value = size

  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex++
  }

  return `${value.toFixed(1)} ${units[unitIndex]}`
}

const displaySize = computed(() => {
  if (props.downloaded !== undefined && props.total !== undefined) {
    return `${formatSize(props.downloaded)} / ${formatSize(props.total)}`
  }
  return ''
})
</script>

<template>
  <div class="progress-wrapper" :class="variant">
    <label v-if="label" class="progress-label">{{ label }}</label>
    <div class="progress-container" :class="`height-${height}`" :data-variant="variant">
      <div
        class="progress-fill"
        :class="[
          `fill-${variant}`,
          {
            animating: animating && variant === 'activity' && !indeterminate,
            indeterminate: indeterminate && variant === 'activity',
          },
        ]"
        :style="{ width: indeterminate ? '35%' : `${Math.max(0, Math.min(100, value))}%` }"
      ></div>
    </div>
    <div v-if="(showPercentage && !indeterminate) || showSize" class="progress-info">
      <div v-if="showPercentage && !indeterminate" class="info-item">
        <span class="percentage">{{ Math.round(value) }}%</span>
      </div>
      <div v-if="showSize && displaySize" class="info-item">
        <span class="size">{{ displaySize }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.progress-wrapper {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  width: 100%;
}

.progress-label {
  font-size: 0.875rem;
  font-weight: 500;
  color: #aaa;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

/* Container heights */
.progress-container {
  width: 100%;
  border-radius: 6px;
  overflow: hidden;
  transition: height 0.2s;
}

.progress-container.height-small {
  height: 6px;
}

.progress-container.height-medium {
  height: 10px;
}

.progress-container.height-large {
  height: 14px;
}

/* Variant styles */
.progress-container[data-variant='default'] {
  background-color: #333;
}

.progress-container[data-variant='storage'] {
  background: #333;
}

.progress-container[data-variant='download'] {
  background-color: rgba(0, 0, 0, 0.3);
  box-shadow: inset 0 1px 3px rgba(0, 0, 0, 0.3);
}

.progress-container[data-variant='activity'] {
  background-color: rgba(0, 0, 0, 0.3);
  box-shadow: inset 0 1px 3px rgba(0, 0, 0, 0.3);
}

/* Fill variants */
.progress-fill {
  height: 100%;
  border-radius: 6px;
  transition: width 0.3s ease;
}

.progress-fill.fill-default {
  background-color: var(--brand-500);
}

.progress-fill.fill-storage {
  background: linear-gradient(90deg, var(--brand-focus), var(--brand-500));
}

.progress-fill.fill-storage.warning {
  background: linear-gradient(90deg, #f39c12, #f1c40f);
}

.progress-fill.fill-storage.danger {
  background: linear-gradient(90deg, #e74c3c, #c0392b);
}

.progress-fill.fill-download {
  background-color: #3498db;
}

.progress-fill.fill-activity {
  background: linear-gradient(90deg, #1e88e5, #42a5f5);
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.2);
}

.progress-fill.fill-activity.animating {
  animation: progress-shimmer 2s infinite;
}

.progress-fill.fill-activity.indeterminate {
  animation: progress-indeterminate 1.4s ease-in-out infinite;
  transition: none;
}

@keyframes progress-indeterminate {
  0% {
    transform: translateX(-120%);
  }
  100% {
    transform: translateX(320%);
  }
}

@keyframes progress-shimmer {
  0%,
  100% {
    background: linear-gradient(90deg, #1e88e5, #42a5f5);
  }
  50% {
    background: linear-gradient(90deg, #42a5f5, #1e88e5);
  }
}

/* Info display */
.progress-info {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
  font-size: 0.8rem;
  flex-wrap: wrap;
}

.info-item {
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.percentage {
  font-weight: 500;
  color: #fff;
  font-size: 0.9rem;
}

.size {
  color: #999;
}

/* Wrapper style variants */
.progress-wrapper.storage {
  margin-bottom: 0.75rem;
}

.progress-wrapper.download {
  margin-bottom: 1rem;
}
</style>
