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
<!-- A selected book that couldn't be loaded when Repair Books opened: in the list with the
     others, in the error state (red bar), saying why. It can't be ticked or searched. Load again
     reads it anew, except for a book deleted since it was selected. -->
<template>
  <div class="form-control-card match-card load-error-card" data-match-state="error">
    <div class="load-error-header">
      <label class="load-error-heading" title="This book couldn't be loaded">
        <input type="checkbox" class="repair-checkbox" :checked="false" disabled />
        <span>{{ title || `Audiobook #${audiobookId}` }}</span>
      </label>
      <button
        v-if="loadError.canLoadAgain"
        type="button"
        class="btn btn-secondary load-again-btn"
        :disabled="isLoadingAgain"
        data-testid="repair-load-again"
        @click="emit('loadAgain')"
      >
        <PhArrowCounterClockwise :size="14" />
        {{ isLoadingAgain ? 'Loading…' : 'Load again' }}
      </button>
    </div>
    <p class="load-error-message" data-testid="repair-load-error">
      <PhWarningCircle :size="16" />
      {{ loadError.message }}
    </p>
    <StatusBar tone="error" />
  </div>
</template>

<script setup lang="ts">
import { PhArrowCounterClockwise, PhWarningCircle } from '@phosphor-icons/vue'
import StatusBar from '@/components/base/StatusBar.vue'
import type { RepairLoadError } from '@/utils/repair'

defineProps<{
  audiobookId: number
  title: string
  loadError: RepairLoadError
  isLoadingAgain: boolean
}>()

const emit = defineEmits<{
  loadAgain: []
}>()
</script>

<style scoped>
/* Room at the bottom for the state bar, as on a book's card */
.form-control-card.load-error-card {
  position: relative;
  overflow: hidden;
  padding-bottom: calc(1rem + 3px);
}

.load-error-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 0.75rem;
}

.load-error-heading {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 600;
  color: #e0e0e0;
  cursor: not-allowed;
}

.load-again-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.375rem;
}

.load-error-message {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin: 0;
  color: var(--status-error);
  font-size: 0.875rem;
}
</style>
