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
<!-- How many books are ticked, of how many, for what (repair on step 1, organize on step 2),
     with Select All and Clear -->
<template>
  <div class="form-group repair-group" data-testid="repair-selection">
    <label class="form-label">
      <PhCheckSquare />
      Selection
    </label>
    <div class="form-control-card">
      <div class="repair-toolbar">
        <p class="repair-toolbar-title" data-testid="repair-selection-count">
          <strong>{{ selected }}</strong> of {{ total }} book(s) to {{ action }}
        </p>
        <div class="toolbar-actions">
          <button
            type="button"
            class="btn btn-secondary repair-action-btn"
            data-testid="repair-select-all"
            :disabled="disabled"
            @click="emit('selectAll')"
          >
            Select All
          </button>
          <button
            type="button"
            class="btn btn-secondary repair-action-btn"
            data-testid="repair-clear"
            :disabled="disabled"
            @click="emit('clear')"
          >
            Clear
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { PhCheckSquare } from '@phosphor-icons/vue'

defineProps<{
  selected: number
  total: number
  // What the ticked books are for
  action: 'repair' | 'organize'
  disabled?: boolean
}>()

const emit = defineEmits<{ selectAll: []; clear: [] }>()
</script>

<style scoped>
.repair-group {
  margin-bottom: 1rem;
}

.repair-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.repair-toolbar-title {
  margin: 0;
  color: #ccc;
}

.toolbar-actions {
  display: flex;
  gap: 0.5rem;
}
</style>
