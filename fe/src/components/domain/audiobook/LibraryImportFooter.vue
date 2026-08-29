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
  <div class="import-footer">
    <div class="footer-left">
      <label class="footer-label">
        Monitor:
        <select v-model="store.monitor" class="mode-select" :disabled="isImporting">
          <option value="all">All</option>
          <option value="none">None</option>
        </select>
      </label>
      <label class="footer-label">
        On import:
        <select v-model="store.action" class="mode-select" :disabled="isImporting">
          <option value="none">Leave files in place</option>
          <option value="move">Move</option>
          <option value="hardlink/copy">Hardlink / Copy</option>
        </select>
        <div v-if="store.action != 'none'">
          <label class="footer-label"
            >to
            <select
              v-model="destinationFolderId"
              class="mode-select destination-select"
              :disabled="isImporting"
            >
              <option v-for="f in props.folders" :key="f.id" :value="f.id">
                {{ f.path }}
              </option>
            </select>
          </label>
        </div>
        <span v-else>Files will be added to the library without being moved or copied</span>
      </label>

      <div v-if="store.metadataFetchCount > 100" class="rate-limit-warning">
        <PhWarning :size="14" />
        {{ store.metadataFetchCount }} API lookups - rate limit: 150/window
      </div>

      <div
        v-if="usesCopyAndRetainMovePolicy"
        class="move-policy-warning"
        data-testid="move-policy-warning"
      >
        <PhWarning :size="16" />
        <span>
          This storage cannot prove durable file identity. Move will copy the selected files and
          retain the source; Listenarr will not attempt source cleanup.
        </span>
      </div>
    </div>

    <div class="footer-center">
      <button
        v-if="store.hasUnprocessedItems && !store.isProcessing"
        class="btn btn-secondary btn-sm"
        :disabled="isImporting"
        @click="store.startProcessing()"
      >
        <PhPlay :size="14" />
        Start Matching
      </button>

      <template v-if="store.isProcessing">
        <PhSpinner class="ph-spin" :size="14" />
        <span class="processing-label">
          Processing {{ store.processedCount }} / {{ store.itemList.length }}...
        </span>
        <button
          class="btn btn-secondary btn-sm"
          :disabled="isImporting"
          @click="store.stopProcessing()"
        >
          <PhStop :size="14" />
          Cancel
        </button>
      </template>
    </div>

    <div class="footer-right">
      <span v-if="displayImportCount > 0" class="selected-label">
        {{ displayImportCount }} selected
      </span>
      <button
        class="btn btn-primary"
        :disabled="
          store.selectedCount === 0 ||
          store.isProcessing ||
          isImporting ||
          !filesystemReadinessStore.filesystemReady
        "
        :title="
          filesystemReadinessStore.filesystemReady
            ? undefined
            : 'Available after library filesystem initialization completes'
        "
        @click="handleImport"
      >
        <PhSpinner v-if="isImporting" class="ph-spin" :size="14" />
        <PhDownload v-else :size="14" />
        {{ importButtonLabel }}
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { PhWarning, PhPlay, PhStop, PhSpinner, PhDownload } from '@phosphor-icons/vue'
import { useLibraryImportStore } from '@/stores/libraryImport'
import { useToast } from '@/services/toastService'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import type { RootFolder } from '@/types'

const props = defineProps<{
  folders: RootFolder[]
  sourceFolder?: RootFolder
}>()

const store = useLibraryImportStore()
const filesystemReadinessStore = useFilesystemReadinessStore()
const toast = useToast()

const destinationFolderId = ref<number | null>(props.folders[0]?.id ?? null)
const isImporting = ref(false)
const importingCount = ref(0)

const destinationPath = computed(() => {
  if (store.action == 'none') return ''
  return props.folders.find((f) => f.id === destinationFolderId.value)?.path ?? ''
})

const destinationFolder = computed(() =>
  props.folders.find((folder) => folder.id === destinationFolderId.value),
)

const usesCopyAndRetainMovePolicy = computed(() => {
  if (store.action !== 'move') return false

  return [props.sourceFolder, destinationFolder.value].some(
    (folder) => folder?.canPublishNewFiles === true && folder.canMutateFilesystem === false,
  )
})

const displayImportCount = computed(() =>
  isImporting.value ? importingCount.value : store.selectedCount,
)

const importButtonLabel = computed(() => {
  const count = displayImportCount.value
  const noun = `Book${count !== 1 ? 's' : ''}`
  if (isImporting.value) {
    return `Importing ${count > 0 ? count : ''} ${noun}...`.replace(/\s+/g, ' ').trim()
  }

  return `Import ${count > 0 ? count : ''} ${noun}`.replace(/\s+/g, ' ').trim()
})

async function handleImport() {
  if (isImporting.value || store.selectedCount === 0 || !filesystemReadinessStore.filesystemReady) {
    return
  }

  importingCount.value = store.selectedCount
  isImporting.value = true

  try {
    const { imported, errors, warnings = [] } = await store.importSelected(destinationPath.value)

    if (imported > 0) {
      let msg = `${imported} book${imported !== 1 ? 's' : ''} imported`
      if (store.metadataFetchCount > 0) msg += ` - ${store.metadataFetchCount} metadata lookups`
      toast.success('Import complete', msg)
    }

    if (errors.length > 0) {
      toast.error(
        'Import errors',
        `${errors.length} item${errors.length !== 1 ? 's' : ''} failed - check logs`,
      )
    }

    if (warnings.length > 0) {
      toast.warning('Import completed with warnings', warnings.join('\n'))
    }
  } finally {
    isImporting.value = false
    importingCount.value = 0
  }
}
</script>

<style scoped>
.import-footer {
  position: sticky;
  bottom: 0;
  background: #1a1a1a;
  border-top: 1px solid #333;
  padding: 0.75rem 1.5rem;
  display: flex;
  align-items: center;
  gap: 1rem;
  z-index: 10;
}

.footer-left {
  display: flex;
  align-items: center;
  gap: 1rem;
  flex: 1;
  flex-wrap: wrap;
}

.footer-label {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.82rem;
  color: #aaa;
  white-space: nowrap;
}

.footer-to {
  color: #888;
}

.mode-select {
  background: #2a2a2a;
  border: 1px solid #444;
  border-radius: 4px;
  color: #e0e0e0;
  font-size: 0.82rem;
  padding: 0.4rem 0.6rem;
  height: var(--control-height, 40px);
  box-sizing: border-box;
  cursor: pointer;
}

.mode-select:disabled {
  cursor: not-allowed;
  opacity: 0.65;
}

.destination-select {
  font-family: monospace;
  font-size: 0.78rem;
  max-width: 280px;
}

.rate-limit-warning {
  display: flex;
  align-items: center;
  gap: 0.3rem;
  font-size: 0.78rem;
  color: #f59e0b;
  background: rgba(245, 158, 11, 0.1);
  border: 1px solid rgba(245, 158, 11, 0.3);
  border-radius: 4px;
  padding: 0.2rem 0.5rem;
}

.move-policy-warning {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  max-width: 620px;
  color: #fbbf24;
  background: rgba(245, 158, 11, 0.1);
  border: 1px solid rgba(245, 158, 11, 0.35);
  border-radius: 4px;
  padding: 0.35rem 0.55rem;
  font-size: 0.78rem;
  white-space: normal;
}

.footer-center {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  color: #888;
  font-size: 0.82rem;
}

.processing-label {
  color: #aaa;
  white-space: nowrap;
}

.footer-right {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.selected-label {
  font-size: 0.82rem;
  color: #888;
  white-space: nowrap;
}

@media (max-width: 640px) {
  .import-footer {
    flex-direction: column;
    align-items: stretch;
    gap: 0.5rem;
    padding: 0.75rem 1rem;
  }

  .footer-left,
  .footer-center,
  .footer-right {
    width: 100%;
    justify-content: flex-start;
  }

  .destination-select {
    max-width: 100%;
    flex: 1;
  }
}
</style>
