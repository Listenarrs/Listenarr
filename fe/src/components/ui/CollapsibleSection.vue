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
<template>
  <section class="collapsible-section">
    <button
      class="section-header"
      type="button"
      @click="toggle"
      :aria-expanded="expanded"
      :aria-controls="contentId"
    >
      <span class="section-title">{{ title }}</span>
      <span v-if="count !== undefined" class="section-count">{{ count }}</span>
      <PhCaretDown class="section-caret" :class="{ collapsed: !expanded }" aria-hidden="true" />
    </button>
    <!-- v-show keeps slot DOM alive so virtual-scroll refs remain valid when Library collapses -->
    <div v-show="expanded" :id="contentId" role="region" :aria-label="title">
      <slot />
    </div>
  </section>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { PhCaretDown } from '@phosphor-icons/vue'

const props = defineProps<{
  title: string
  count?: number
  sectionKey: string
}>()

const contentId = computed(() => `section-content-${props.sectionKey}`)
const STORAGE_PREFIX = 'books.section.'

function readStored(): boolean {
  try {
    const raw = localStorage.getItem(STORAGE_PREFIX + props.sectionKey)
    if (raw !== null) return raw !== 'false'
  } catch {}
  return true // default: expanded
}

const expanded = ref(readStored())

function toggle() {
  expanded.value = !expanded.value
  try {
    localStorage.setItem(STORAGE_PREFIX + props.sectionKey, expanded.value ? 'true' : 'false')
  } catch {}
}

defineExpose({ expanded, toggle })
</script>

<style scoped>
.collapsible-section {
  margin-bottom: 4px;
}

.section-header {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 10px 20px;
  background: var(--bg-tertiary);
  border: none;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
  color: var(--text-primary);
  cursor: pointer;
  text-align: left;
  font-size: 0.875rem;
  font-weight: 600;
  transition: background 0.15s ease;
}

.section-header:hover {
  background: var(--bg-surface);
}

.section-header:focus-visible {
  outline: 2px solid var(--brand-500);
  outline-offset: -2px;
}

.section-title {
  flex: 1;
}

.section-count {
  font-size: 0.75rem;
  font-weight: 500;
  color: var(--text-secondary);
  background: var(--bg-surface);
  border-radius: 10px;
  padding: 2px 8px;
  min-width: 24px;
  text-align: center;
}

.section-caret {
  flex-shrink: 0;
  transition: transform 0.2s ease;
  color: var(--text-secondary);
}

.section-caret.collapsed {
  transform: rotate(-90deg);
}
</style>
