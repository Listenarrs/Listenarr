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
import { computed, onMounted } from 'vue'
import { storeToRefs } from 'pinia'
import { PhMagnifyingGlass, PhCircleNotch } from '@phosphor-icons/vue'
import { useSearchActivityStore } from '@/stores/searchActivity'

const store = useSearchActivityStore()
const { current, isSearching } = storeToRefs(store)

onMounted(() => store.start())

// The live line: the current message while a sweep is active, otherwise a muted
// idle label (preferring the backend's idle summary if we have one).
const label = computed(() => {
  const c = current.value
  if (!c) return 'Search idle'
  if (c.stage === 'idle') return c.message || 'Search idle'
  return c.message
})

const idle = computed(() => !isSearching.value)
</script>

<template>
  <div
    class="sidebar-search-activity"
    :class="{ active: isSearching, idle }"
    :title="idle ? 'Automatic search is idle' : label"
  >
    <span class="ssa-icon">
      <PhCircleNotch v-if="isSearching" class="ssa-spin" :size="15" weight="bold" />
      <PhMagnifyingGlass v-else :size="15" />
    </span>
    <span class="ssa-text">{{ label }}</span>
  </div>
</template>

<style scoped>
.sidebar-search-activity {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.45rem 0.75rem;
  margin: 0 0.5rem 0.25rem;
  border-radius: 8px;
  font-size: 0.72rem;
  line-height: 1.25;
  color: #aeb6c2;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid transparent;
  transition:
    background-color 0.2s ease,
    color 0.2s ease,
    border-color 0.2s ease;
  overflow: hidden;
}

.sidebar-search-activity.active {
  color: var(--brand, #5aa9e6);
  border-color: rgba(var(--brand-rgb, 90, 169, 230), 0.35);
  background: rgba(var(--brand-rgb, 90, 169, 230), 0.1);
}

.sidebar-search-activity.idle .ssa-icon {
  opacity: 0.6;
}

.ssa-icon {
  display: inline-flex;
  flex: 0 0 auto;
  align-items: center;
}

.ssa-text {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.ssa-spin {
  animation: ssa-rotate 0.9s linear infinite;
}

@keyframes ssa-rotate {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .ssa-spin {
    animation: none;
  }
}
</style>
