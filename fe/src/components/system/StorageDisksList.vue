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
import { ProgressBar } from '@/components/base'
import type { DiskStorageInfo } from '@/types'

defineProps<{
  disks: DiskStorageInfo[]
}>()
</script>

<template>
  <div class="storage-disks">
    <div
      v-for="(disk, index) in disks"
      :key="`${disk.label}:${disk.path}:${index}`"
      class="disk-entry"
    >
      <div class="disk-header">
        <span class="disk-label">{{ disk.label }}</span>
        <span class="disk-path">{{ disk.path }}</span>
        <span v-if="disk.status === 'available'" class="disk-free">
          {{ disk.freeFormatted }} free of {{ disk.totalFormatted }}
        </span>
      </div>
      <ProgressBar
        v-if="disk.status === 'available'"
        :value="disk.usedPercentage"
        variant="storage"
        height="large"
        show-percentage
      />
      <span v-else class="unavailable-tag">unavailable</span>
    </div>
  </div>
</template>

<style scoped>
.storage-disks {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.disk-entry {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0.5rem 0.75rem;
  background: #252525;
  border-radius: 6px;
}

.disk-header {
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
  min-width: 0;
}

.disk-label {
  color: #fff;
  font-weight: 500;
  white-space: nowrap;
}

.disk-path {
  color: #999;
  font-size: 0.8rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.disk-free {
  margin-left: auto;
  flex-shrink: 0;
  color: #ccc;
  font-size: 0.8rem;
  white-space: nowrap;
}

.unavailable-tag {
  color: #f39c12;
  font-size: 0.8rem;
  font-style: italic;
}
</style>
