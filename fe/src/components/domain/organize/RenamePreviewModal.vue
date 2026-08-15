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
  <Modal :visible="visible" size="lg" @close="handleClose">
    <template #header>
      <ModalHeader title="Organize Files" :icon="PhFolderOpen" @close="handleClose" />
    </template>

    <template #default>
      <ModalBody compact maxHeight="72vh" class="organize-modal-body">
        <div v-if="loading" class="organize-state">
          <PhSpinner class="ph-spin organize-icon" />
          <p>Computing expected paths…</p>
        </div>

        <div v-else-if="error" class="organize-state organize-error">
          <PhWarningCircle class="organize-icon" />
          <p>{{ error }}</p>
        </div>

        <div v-else-if="executing" class="organize-state">
          <PhSpinner class="ph-spin organize-icon" />
          <p>Organizing selected audiobooks…</p>
        </div>

        <div
          v-else-if="loaded && changedPreviews.length === 0"
          class="organize-state organize-success"
        >
          <PhCheckCircle class="organize-icon" />
          <p>Everything already matches the current naming pattern.</p>
        </div>

        <div v-else-if="loaded" class="organize-preview">
          <div class="info-section organize-info-section">
            <PhInfo />
            <p>
              Review the proposed folder and filename changes before organizing files on disk.
              Selected items will be updated to match your current naming settings.
            </p>
          </div>

          <div class="form-group organize-group">
            <label class="form-label">
              <PhCheckSquare />
              Selection
            </label>
            <div class="form-control-card">
              <div class="organize-toolbar">
                <p class="organize-toolbar-title">
                  <strong>{{ selectedCount }}</strong> of {{ changedPreviews.length }} audiobook(s)
                  selected
                </p>
                <div class="toolbar-actions">
                  <button
                    type="button"
                    class="btn btn-secondary organize-action-btn"
                    @click="selectAll"
                  >
                    Select All
                  </button>
                  <button
                    type="button"
                    class="btn btn-secondary organize-action-btn"
                    @click="clearSelection"
                  >
                    Clear
                  </button>
                </div>
              </div>
            </div>
          </div>

          <div class="form-group organize-group">
            <label class="form-label">
              <PhFolderOpen />
              Preview
            </label>
            <div class="organize-preview-list">
              <div
                v-for="preview in changedPreviews"
                :key="preview.audiobookId"
                class="form-control-card preview-card"
              >
                <label class="preview-header">
                  <span class="preview-heading">
                    <input
                      type="checkbox"
                      class="preview-checkbox"
                      :checked="selected.has(preview.audiobookId)"
                      @change="toggleSelected(preview.audiobookId)"
                    />
                    <span class="preview-title">{{
                      preview.audiobookTitle || `Audiobook #${preview.audiobookId}`
                    }}</span>
                  </span>
                  <span class="preview-meta">{{ previewChangeSummary(preview) }}</span>
                </label>

                <div v-if="preview.folderChanged" class="preview-section">
                  <span class="preview-label">Folder</span>
                  <RenamePathDiff
                    :old-path="preview.currentFolderPath"
                    :new-path="preview.newFolderPath"
                  />
                </div>

                <div
                  v-for="file in preview.fileRenames.filter((entry) => entry.changed)"
                  :key="file.fileId"
                  class="preview-section"
                >
                  <span class="preview-label">File</span>
                  <RenamePathDiff :old-path="file.currentFilename" :new-path="file.newFilename" />
                </div>
              </div>
            </div>
          </div>
        </div>

        <div v-if="finished && results.length > 0" class="form-group organize-results-group">
          <label class="form-label">
            <PhCheckCircle />
            Results
          </label>
          <div class="form-control-card results-list">
            <div
              v-for="result in results"
              :key="result.audiobookId"
              class="result-row"
              :class="{ success: result.success, error: !result.success }"
            >
              <component
                :is="result.success ? PhCheckCircle : PhWarningCircle"
                class="result-icon"
              />
              <span class="result-title">{{ titleFor(result.audiobookId) }}</span>
              <span class="result-detail">
                {{ result.success ? 'Organized successfully' : result.error || 'Organize failed' }}
              </span>
            </div>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button type="button" class="btn cancel-button" @click="handleClose">
            <PhX :size="16" />
            {{ finished ? 'Close' : 'Cancel' }}
          </button>
        </template>
        <template #default>
          <button
            v-if="!finished"
            type="button"
            class="btn btn-primary"
            :disabled="
              loading ||
              executing ||
              selectedCount === 0 ||
              !filesystemReadinessStore.filesystemReady
            "
            :title="
              !filesystemReadinessStore.filesystemReady
                ? 'Available after library filesystem initialization completes'
                : undefined
            "
            @click="confirm"
          >
            <PhSpinner v-if="executing" class="ph-spin" :size="16" />
            <PhFolderOpen v-else :size="16" />
            {{ executing ? 'Organizing…' : `Organize ${selectedCount}` }}
          </button>
          <button v-else type="button" class="btn btn-primary" @click="handleDone">
            <PhCheckCircle :size="16" />
            Done
          </button>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import {
  PhCheckCircle,
  PhCheckSquare,
  PhFolderOpen,
  PhInfo,
  PhSpinner,
  PhWarningCircle,
  PhX,
} from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/feedback'
import RenamePathDiff from './RenamePathDiff.vue'
import { apiService } from '@/services/api'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import type { RenameOperation, RenamePreview, RenameResult } from '@/types'

const props = withDefaults(
  defineProps<{
    visible?: boolean
    audiobookIds?: number[]
  }>(),
  {
    visible: false,
    audiobookIds: () => [],
  },
)

const emit = defineEmits<{
  close: []
  done: []
}>()

const filesystemReadinessStore = useFilesystemReadinessStore()
const loading = ref(false)
const loaded = ref(false)
const executing = ref(false)
const finished = ref(false)
const error = ref<string | null>(null)
const previews = ref<RenamePreview[]>([])
const results = ref<RenameResult[]>([])
const selected = ref<Set<number>>(new Set())

const changedPreviews = computed(() => previews.value.filter((preview) => preview.hasChanges))
const selectedCount = computed(
  () => changedPreviews.value.filter((preview) => selected.value.has(preview.audiobookId)).length,
)

watch(
  () => props.visible,
  async (visible) => {
    if (visible && props.audiobookIds.length > 0) {
      await load()
    } else if (!visible) {
      reset()
    }
  },
  { immediate: true },
)

async function load() {
  loading.value = true
  loaded.value = false
  executing.value = false
  finished.value = false
  error.value = null
  results.value = []

  try {
    previews.value = await apiService.previewRename(props.audiobookIds)
    selected.value = new Set(changedPreviews.value.map((preview) => preview.audiobookId))
    loaded.value = true
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load organize preview.'
  } finally {
    loading.value = false
  }
}

async function confirm() {
  const operations: RenameOperation[] = changedPreviews.value
    .filter((preview) => selected.value.has(preview.audiobookId))
    .map((preview) => ({
      audiobookId: preview.audiobookId,
      currentFolderPath: preview.currentFolderPath,
      currentFolderSemantics: preview.currentFolderSemantics,
      newFolderPath: preview.folderChanged ? preview.newFolderPath : undefined,
      fileRenames: preview.fileRenames
        .filter((entry) => entry.changed)
        .map((entry) => ({
          fileId: entry.fileId,
          currentPath: entry.currentPath || '',
          newPath: entry.newPath || '',
        })),
    }))

  if (operations.length === 0) {
    return
  }

  executing.value = true
  error.value = null
  try {
    results.value = await apiService.executeRename(operations)
    finished.value = true
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to organize files.'
  } finally {
    executing.value = false
  }
}

function selectAll() {
  selected.value = new Set(changedPreviews.value.map((preview) => preview.audiobookId))
}

function clearSelection() {
  selected.value = new Set()
}

function toggleSelected(id: number) {
  const next = new Set(selected.value)
  if (next.has(id)) {
    next.delete(id)
  } else {
    next.add(id)
  }
  selected.value = next
}

function titleFor(audiobookId: number) {
  return (
    previews.value.find((preview) => preview.audiobookId === audiobookId)?.audiobookTitle ||
    `Audiobook #${audiobookId}`
  )
}

function previewChangeSummary(preview: RenamePreview) {
  const parts: string[] = []
  const changedFiles = preview.fileRenames.filter((entry) => entry.changed).length

  if (preview.folderChanged) {
    parts.push('Folder')
  }

  if (changedFiles > 0) {
    parts.push(`${changedFiles} file${changedFiles === 1 ? '' : 's'}`)
  }

  return parts.join(' + ') || 'No changes'
}

function handleDone() {
  emit('done')
}

function handleClose() {
  if (finished.value) {
    emit('done')
  } else {
    emit('close')
  }
}

function reset() {
  loading.value = false
  loaded.value = false
  executing.value = false
  finished.value = false
  error.value = null
  previews.value = []
  results.value = []
  selected.value = new Set()
}
</script>

<style scoped>
.info-section {
  display: flex;
  align-items: flex-start;
  gap: 0.6rem;
  padding: 0.75rem;
  background-color: rgba(52, 152, 219, 0.09);
  border: 1px solid rgba(52, 152, 219, 0.28);
  border-radius: 6px;
  color: #3498db;
}

.info-section p {
  margin: 0;
  font-size: 0.95rem;
  line-height: 1.4;
  color: #ccc;
}

.info-section strong {
  color: #5dade2;
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.form-label {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
  font-weight: 500;
  margin: 0;
}

.organize-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.75rem;
  padding: 2rem;
  text-align: center;
}

.organize-icon {
  width: 28px;
  height: 28px;
}

.organize-error {
  color: var(--text-danger, #ef5350);
}

.organize-success {
  color: var(--text-success, #66bb6a);
}

.organize-preview {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
}

.organize-info-section {
  margin-bottom: 0;
}

.organize-group {
  margin-bottom: 0;
}

.organize-toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
  flex-wrap: wrap;
}

.organize-toolbar-title {
  margin: 0;
  color: var(--text-secondary, #c7ced8);
  line-height: 1.4;
}

.toolbar-actions {
  display: flex;
  gap: 0.75rem;
  align-items: center;
}

.organize-action-btn {
  min-height: 36px;
  padding: 0.45rem 0.85rem;
  font-size: 0.88rem;
}

.organize-preview-list {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.preview-card {
  padding: 1rem;
}

.preview-card > * + * {
  margin-top: 0.75rem;
}

.preview-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 0.85rem;
  margin-bottom: 0.9rem;
  cursor: pointer;
}

.preview-heading {
  display: inline-flex;
  align-items: flex-start;
  gap: 0.65rem;
  min-width: 0;
}

.preview-checkbox {
  margin-top: 0.15rem;
}

.preview-title {
  font-weight: 600;
  color: var(--text-primary, #f3f6fb);
  line-height: 1.4;
}

.preview-meta {
  flex-shrink: 0;
  color: var(--text-muted, #97a2af);
  font-size: 0.82rem;
  font-weight: 500;
  line-height: 1.4;
}

.preview-section {
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
  margin-top: 0;
}

.preview-label {
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--text-muted, #97a2af);
}

.results-list {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.result-row {
  display: grid;
  grid-template-columns: 20px minmax(0, 1fr) auto;
  align-items: center;
  gap: 0.75rem;
  padding: 0.65rem 0.85rem;
  border-radius: 8px;
}

.result-row.success {
  background: rgba(102, 187, 106, 0.08);
  color: var(--text-success, #66bb6a);
}

.result-row.error {
  background: rgba(239, 83, 80, 0.08);
  color: var(--text-danger, #ef5350);
}

.result-icon {
  width: 18px;
  height: 18px;
}

.result-title {
  font-weight: 600;
}

.result-detail {
  color: var(--text-secondary, #c7ced8);
}

.cancel-button {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

@media (max-width: 768px) {
  .toolbar-actions {
    width: 100%;
    justify-content: flex-start;
  }

  .preview-header {
    flex-direction: column;
    align-items: stretch;
  }

  .preview-meta {
    align-self: flex-start;
  }
}
</style>
