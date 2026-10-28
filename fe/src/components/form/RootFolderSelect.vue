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
  <div class="root-folder-select">
    <label v-if="!hideLabel" class="form-label">Root Folder</label>

    <div v-if="store.loading" class="loading-row">
      <PhSpinner class="ph-spin" />
      <span>Loading root folders...</span>
    </div>

    <div v-else :class="['root-select-content', inline ? 'inline' : '']">
      <select class="form-select" :value="selectValue" @change="onChange">
        <option :value="NULL_VALUE">Use default</option>
        <option v-for="folder in store.folders" :key="folder.id" :value="String(folder.id)">
          {{ folder.name }} — {{ folder.path }}
        </option>
      </select>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { PhSpinner } from '@phosphor-icons/vue'
import { useRootFoldersStore } from '@/stores/rootFolders'

const NULL_VALUE = '__null__'

const props = withDefaults(
  defineProps<{
    rootId?: number | null
    hideLabel?: boolean
    inline?: boolean
  }>(),
  {
    rootId: null,
    hideLabel: false,
    inline: false,
  },
)

const emit = defineEmits<{
  'update:rootId': [value: number | null]
}>()

const store = useRootFoldersStore()

onMounted(() => {
  void store.load()
})

const selectValue = computed(() => (props.rootId == null ? NULL_VALUE : String(props.rootId)))

function onChange(event: Event) {
  const value = (event.target as HTMLSelectElement).value
  if (value === NULL_VALUE) {
    emit('update:rootId', null)
    return
  }

  const id = Number(value)
  if (!Number.isNaN(id)) {
    emit('update:rootId', id)
  }
}
</script>

<style scoped>
.root-folder-select {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.root-select-content.inline {
  display: flex;
  gap: 0.5rem;
  align-items: center;
  flex-wrap: wrap;
  width: 100%;
}

.form-select {
  padding: 0.75rem 1rem;
  height: 40px;
  box-sizing: border-box;
  background-color: #1a1a1a;
  border: 1px solid #333;
  border-radius: 6px;
  color: white;
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
}

.form-select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.2);
}

.loading-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #adb5bd;
}
</style>
