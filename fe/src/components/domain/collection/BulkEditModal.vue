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
  <Modal :visible="isOpen" size="md" @close="close">
    <template #header>
      <ModalHeader :title="'Bulk Edit Audiobooks'" :icon="PhPencil" @close="close" />
    </template>

    <template #default>
      <ModalBody>
        <div class="info-section">
          <PhInfo />
          <p>
            Editing <strong>{{ selectedCount }}</strong> audiobook{{
              selectedCount !== 1 ? 's' : ''
            }}. Only the fields you change will be updated.
          </p>
        </div>

        <form @submit.prevent="handleSave" class="edit-form">
          <!-- Monitored Status -->
          <div class="form-group">
            <label class="form-label">
              <PhEye />
              Monitored Status
            </label>
            <div class="radio-group">
              <label class="radio-label">
                <input type="radio" v-model="formData.monitored" :value="null" name="monitored" />
                <span>No Change</span>
              </label>
              <label class="radio-label">
                <input type="radio" v-model="formData.monitored" :value="true" name="monitored" />
                <span>Monitored</span>
              </label>
              <label class="radio-label">
                <input type="radio" v-model="formData.monitored" :value="false" name="monitored" />
                <span>Unmonitored</span>
              </label>
            </div>
            <p class="help-text">
              Monitored audiobooks will be automatically upgraded when better quality releases are
              found
            </p>
          </div>

          <!-- Quality Profile -->
          <div class="form-group">
            <label class="form-label" for="quality-profile">
              <PhStar />
              Quality Profile
            </label>
            <select id="quality-profile" v-model="formData.qualityProfileId" class="form-select">
              <option :value="null">No Change</option>
              <option v-for="profile in qualityProfiles" :key="profile.id" :value="profile.id">
                {{ profile.name }}{{ profile.isDefault ? ' (Default)' : '' }}
              </option>
            </select>
            <p class="help-text">
              Controls which quality standards to use for downloads and upgrades
            </p>
          </div>

          <!-- Root Folder -->
          <div class="form-group">
            <label class="form-label">
              <PhFolder />
              Root Folder
            </label>

            <label class="radio-label">
              <Checkbox v-model="formData.rootChangeEnabled">Change root folder</Checkbox>
            </label>

            <div v-if="formData.rootChangeEnabled" class="mt-md">
              <RootFolderSelect v-model:rootId="formData.rootId" data-cy="bulk-root-select" />

              <p class="help-text">
                Select a configured root. If you choose "Use default" and no named default exists,
                the application default output path will be used.
              </p>
              <p v-if="resolvedRootPath" class="help-text" data-testid="effective-destination-root">
                Destination root: <code>{{ resolvedRootPath }}</code>
              </p>
            </div>
          </div>

          <!-- Action Buttons moved to footer -->
        </form>

        <div v-if="showResults" class="results-section">
          <h4>Bulk Update Results</h4>
          <p>
            {{ successfulResults.length }} succeeded, {{ partialResults.length }} partially
            succeeded, {{ failedResults.length }} failed
          </p>

          <div class="results-list">
            <div v-for="res in results" :key="res.id" class="result-item">
              <div class="result-header">
                <strong>Audiobook ID {{ res.id }}</strong>
                <span
                  class="status"
                  :class="{
                    success: res.success,
                    partial: isPartialResult(res),
                    error: !res.success && !isPartialResult(res),
                  }"
                  >{{ resultStatusLabel(res) }}</span
                >
              </div>
              <p v-if="isPartialResult(res)" class="partial-message">
                {{ partialResultMessage(res) }}
              </p>
              <ul v-if="res.errors && res.errors.length > 0" class="error-list">
                <li v-for="(err, idx) in res.errors" :key="idx">{{ err }}</li>
              </ul>
            </div>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <button type="button" class="cancel-button btn" @click="close"><PhX /> Cancel</button>
      <button
        type="button"
        class="btn btn-primary"
        :disabled="saving || !hasChanges"
        @click="handleSave"
      >
        <PhSpinner v-if="saving" class="ph-spin" />
        <PhCheck v-else /> {{ saving ? 'Saving...' : 'Save Changes' }}
      </button>
    </template>
  </Modal>

  <MoveAudiobookModal
    :visible="showMoveConfirm"
    :pendingRootPath="pendingRootPath"
    v-model:moveFiles="modalMoveFiles"
    v-model:deleteEmpty="modalDeleteEmpty"
    @cancel="cancelMoveConfirm"
    @confirm="
      (payload) => {
        if (payload?.moveFiles) confirmMove()
        else confirmChangeWithoutMoving()
      }
    "
  />
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import Checkbox from '@/components/form/Checkbox.vue'
import { Modal, ModalBody, ModalHeader } from '@/components/feedback'
import MoveAudiobookModal from '@/components/feedback/MoveAudiobookModal.vue'
import { apiService } from '@/services/api'
import { useMoveJobsStore } from '@/stores/moveJobs'
import { executeBulkEdit, type BulkEditItemResult } from '@/utils/bulkEditOrchestration'
import { buildApiPath } from '@/services/apiBase'
import { useToast } from '@/services/toastService'
import type { QualityProfile } from '@/types'
import { useRootFoldersStore } from '@/stores/rootFolders'
import RootFolderSelect from '@/components/form/RootFolderSelect.vue'

interface Props {
  isOpen: boolean
  selectedCount: number
  selectedIds: Set<number>
}

interface FormData {
  monitored: boolean | null
  qualityProfileId: number | null
  // root change controls
  rootChangeEnabled: boolean
  rootId: number | null
}

const props = defineProps<Props>()
const emit = defineEmits<{
  close: []
  saved: []
}>()

const qualityProfiles = ref<QualityProfile[]>([])
const rootStore = useRootFoldersStore()
const moveJobsStore = useMoveJobsStore()
const saving = ref(false)

// Root change helper values
const defaultOutputPath = ref<string | null>(null)
const results = ref<BulkEditItemResult[]>([])
const showResults = ref(false)
const bulkUpdateEndpoint = buildApiPath('/library/bulk-update')

// Move confirmation modal state
const showMoveConfirm = ref(false)
const pendingRootPath = ref<string | null>(null)
const modalMoveFiles = ref(true)
const modalDeleteEmpty = ref(true)
let moveConfirmResolver:
  | ((r: { proceed: boolean; moveFiles: boolean; deleteEmptySource: boolean }) => void)
  | null = null

const formData = ref<FormData>({
  monitored: null,
  qualityProfileId: null,
  rootChangeEnabled: false,
  rootId: null,
})

const resolvedRootPath = computed(() => {
  if (!formData.value.rootChangeEnabled) return null
  if (formData.value.rootId && formData.value.rootId > 0) {
    return rootStore.folders.find((folder) => folder.id === formData.value.rootId)?.path ?? null
  }
  return defaultOutputPath.value
})

const hasChanges = computed(() => {
  return (
    formData.value.monitored !== null ||
    formData.value.qualityProfileId !== null ||
    formData.value.rootChangeEnabled === true
  )
})

const toast = useToast()
const successfulResults = computed(() => results.value.filter((result) => result.success))
const partialResults = computed(() => results.value.filter(isPartialResult))
const failedResults = computed(() =>
  results.value.filter((result) => !result.success && !isPartialResult(result)),
)

watch(
  () => props.isOpen,
  async (isOpen) => {
    if (isOpen) {
      await loadData()
      resetForm()
    }
  },
)

async function loadData() {
  try {
    // Load quality profiles
    qualityProfiles.value = await apiService.getQualityProfiles()

    // Load root folders from configuration
    await rootStore.load()
    if (rootStore.folders.length > 0) {
      // Capture default output path for fallback when user picks "Use default"
      const def = rootStore.folders.find((f) => f.isDefault)
      defaultOutputPath.value = def?.path ?? null
    } else {
      const appSettings = await apiService.getApplicationSettings()
      defaultOutputPath.value = appSettings.outputPath || null
    }
  } catch (error) {
    console.error('Failed to load bulk edit data:', error)
  }
}

function resetForm() {
  formData.value = {
    monitored: null,
    qualityProfileId: null,
    rootChangeEnabled: false,
    rootId: null,
  }
}

function askMoveConfirmation(newRootPath: string) {
  modalMoveFiles.value = true
  modalDeleteEmpty.value = true
  pendingRootPath.value = newRootPath
  showMoveConfirm.value = true
  return new Promise<{ proceed: boolean; moveFiles: boolean; deleteEmptySource: boolean }>(
    (resolve) => {
      moveConfirmResolver = resolve
    },
  )
}

function cancelMoveConfirm() {
  if (moveConfirmResolver)
    moveConfirmResolver({ proceed: false, moveFiles: false, deleteEmptySource: false })
  moveConfirmResolver = null
  showMoveConfirm.value = false
  pendingRootPath.value = null
}

function confirmChangeWithoutMoving() {
  if (moveConfirmResolver)
    moveConfirmResolver({ proceed: true, moveFiles: false, deleteEmptySource: false })
  moveConfirmResolver = null
  showMoveConfirm.value = false
  pendingRootPath.value = null
}

function confirmMove() {
  if (moveConfirmResolver)
    moveConfirmResolver({
      proceed: true,
      moveFiles: Boolean(modalMoveFiles.value),
      deleteEmptySource: Boolean(modalDeleteEmpty.value),
    })
  moveConfirmResolver = null
  showMoveConfirm.value = false
  pendingRootPath.value = null
}

import { logger } from '@/utils/logger'
import {
  PhX,
  PhSpinner,
  PhCheck,
  PhPencil,
  PhInfo,
  PhEye,
  PhStar,
  PhFolder,
} from '@phosphor-icons/vue'

async function handleSave() {
  if (!hasChanges.value) return

  saving.value = true
  try {
    const updates: Record<string, boolean | number | string> = {}
    if (formData.value.monitored !== null) {
      updates.monitored = formData.value.monitored
    }
    if (formData.value.qualityProfileId !== null) {
      updates.qualityProfileId = formData.value.qualityProfileId
    }

    let userWantsMove = false
    let userWantsDeleteEmpty = false
    let newRootPath: string | null = null
    if (formData.value.rootChangeEnabled === true) {
      newRootPath = resolvedRootPath.value
      if (!newRootPath) {
        throw new Error('Select a valid destination root before saving.')
      }

      const choice = await askMoveConfirmation(newRootPath)
      if (!choice?.proceed) return
      userWantsMove = Boolean(choice.moveFiles)
      userWantsDeleteEmpty = Boolean(choice.deleteEmptySource)
      updates.rootFolder = newRootPath
    }

    const ids = Array.from(props.selectedIds)
    logger.debug('[BulkEditModal] Preparing bulk update', {
      endpoint: bulkUpdateEndpoint,
      ids,
      updates,
      physicalMove: userWantsMove,
      timestamp: new Date().toISOString(),
    })

    const outcome = await executeBulkEdit(
      {
        ids,
        updates,
        destinationRoot: newRootPath,
        moveFiles: userWantsMove,
        deleteEmptySource: userWantsDeleteEmpty,
      },
      {
        bulkUpdateAudiobooks: (audiobookIds, metadataUpdates, pathChange) =>
          apiService.bulkUpdateAudiobooks(audiobookIds, metadataUpdates, pathChange),
        trackQueuedJob: (job) => moveJobsStore.trackQueuedJob(job),
      },
    )

    results.value = outcome.results
    showResults.value = true
    const successCount = successfulResults.value.length
    const partialCount = partialResults.value.length
    const failureCount = failedResults.value.length
    if (partialCount > 0 || failureCount > 0) {
      toast.error(
        'Bulk update incomplete',
        `${successCount} succeeded, ${partialCount} partially succeeded, and ${failureCount} failed. Review the per-audiobook results.`,
      )
      return
    }

    if (userWantsMove) {
      toast.info(
        'Move jobs queued',
        `Queued ${successCount} move job(s). Files will be moved in the background.`,
      )
    } else {
      toast.success('Bulk update', `Updated ${successCount} audiobook(s)`)
    }

    emit('saved')
    close()
  } catch (error) {
    try {
      const err = error as Error & { url?: string }
      console.error('[BulkEditModal] Failed to save bulk edits:', {
        name: err?.name,
        message: err?.message,
        stack: err?.stack,
        url: err?.url || bulkUpdateEndpoint,
      })
    } catch {
      console.error('Failed to save bulk edits (minimal):', error)
    }

    let message = 'Failed to save changes. Please try again.'
    try {
      const err = error as Error & { body?: string }
      if (err.body) {
        try {
          const parsed = JSON.parse(err.body)
          if (parsed?.message) message = parsed.message
        } catch {
          message = err.body
        }
      } else if (err.message) {
        message = err.message
      }
    } catch {
      // Keep the generic message when an error cannot be inspected.
    }
    toast.error('Bulk update failed', message)
  } finally {
    saving.value = false
  }
}

function isPartialResult(result: BulkEditItemResult): boolean {
  return !result.success && result.metadataUpdated === true
}

function resultStatusLabel(result: BulkEditItemResult): 'Success' | 'Partial' | 'Failed' {
  if (result.success) return 'Success'
  return isPartialResult(result) ? 'Partial' : 'Failed'
}

function partialResultMessage(result: BulkEditItemResult): string {
  switch (result.pathChangeOutcome) {
    case 'failed':
      return 'Metadata saved; the requested path change failed.'
    case 'not-enqueued':
      return 'Metadata saved; the requested move was not queued.'
    default:
      return 'Metadata saved; the requested path change did not complete.'
  }
}

function close() {
  // Reset results when closing
  results.value = []
  showResults.value = false
  emit('close')
}
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0, 0, 0, 0.75);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 2rem;
}

.modal-container {
  background-color: #1e1e1e;
  border-radius: 6px;
  width: 100%;
  max-width: 600px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
}

.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1.5rem 2rem;
  border-bottom: 1px solid #3a3a3a;
}

.modal-header h2 {
  margin: 0;
  color: white;
  font-size: 1.5rem;
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.btn-close {
  background: none;
  border: none;
  color: #ccc;
  cursor: pointer;
  padding: 0.5rem;
  border-radius: 6px;
  transition: all 0.2s;
  font-size: 1.5rem;
  display: flex;
  align-items: center;
  justify-content: center;
}

.btn-close:hover {
  background-color: #3a3a3a;
  color: white;
}

.modal-body {
  padding: 2rem;
  overflow-y: auto;
  flex: 1;
}

.results-section {
  margin-top: 1.5rem;
  padding: 1rem;
  background: #141414;
  border: 1px solid #2a2a2a;
  border-radius: 6px;
}

.results-list {
  margin-top: 0.75rem;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.result-item {
  padding: 0.75rem;
  background: #1a1a1a;
  border: 1px solid #2e2e2e;
  border-radius: 6px;
}

.result-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
}

.status.success {
  color: #4caf50;
}

.status.error {
  color: #f44336;
}

.status.partial,
.partial-message {
  color: #ffb74d;
}

.partial-message {
  margin: 0.5rem 0 0;
}

.error-list {
  margin: 0.5rem 0 0 0;
  padding-left: 1.25rem;
  color: #f44336;
}

.info-section {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 1rem;
  background-color: rgba(52, 152, 219, 0.1);
  border: 1px solid rgba(52, 152, 219, 0.3);
  border-radius: 6px;
  margin-bottom: 2rem;
  color: #3498db;
}

.info-section svg {
  width: 20px;
  height: 20px;
  flex-shrink: 0;
  margin-top: 0.125rem;
  fill: currentColor;
}

.info-section p {
  margin: 0;
  color: #ccc;
  line-height: 1.5;
}

.info-section strong {
  color: white;
}

.edit-form {
  display: flex;
  flex-direction: column;
  gap: 2rem;
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.form-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: white;
  font-weight: 500;
  font-size: 0.95rem;
}

.form-label svg {
  color: var(--brand-500);
  width: 18px;
  height: 18px;
}

.radio-group {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.radio-label {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  background-color: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s;
  color: #ccc;
}

.radio-label:hover {
  background-color: #333;
  border-color: var(--brand-500);
}

.radio-label input[type='radio'] {
  width: 18px;
  height: 18px;
  cursor: pointer;
  accent-color: var(--brand-500);
}

.radio-label input[type='radio']:checked + span {
  color: white;
  font-weight: 500;
}

.form-select {
  padding: 0.75rem 1rem;
  background-color: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  color: white;
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
}

.form-select:hover {
  border-color: #555;
}

.form-select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.help-text {
  /* rely on centralized modal spacing; use compact variant if needed */
  font-size: 0.85rem;
  color: #999;
  line-height: 1.4;
}

/* modal-footer centralized in src/assets/modals.css; keep local spacing adjustment */
.modal-footer {
  margin-top: 1rem;
}

/* Base .btn styles centralized in src/assets/modals.css; keeping this file's spacing adjustments only */
.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Button color variants are centralized in `src/assets/modals.css` - use `.btn` / `.btn-primary`. */

.btn i.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/main.css */

/* Move confirmation modal styles — now aligned with centralized modal styles */
.confirm-overlay.separate-modal {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.85);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 1rem;
}

.confirm-dialog {
  background: #2a2a2a;
  border-radius: 6px;
  box-shadow: 0 10px 40px rgba(0, 0, 0, 0.5);
  max-width: 700px; /* standardized to modal-md */
  width: 100%;
  max-height: 90vh;
  overflow-y: auto;
  border: 1px solid #444;
}

.confirm-header {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 1.25rem 1.5rem 1rem;
  border-bottom: 1px solid #444;
  margin-bottom: 0;
}
.confirm-header i {
  font-size: 1.5rem;
  color: var(--brand-500);
}

.confirm-header h3 {
  margin: 0;
  font-size: 1.25rem;
  font-weight: 500;
  color: #ffffff;
}

.confirm-body {
  padding: 1.5rem;
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.confirm-description p {
  margin: 0;
  color: #cccccc;
  line-height: 1.5;
}

.path-comparison {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  background: #252526;
  border-radius: 8px;
  padding: 1rem;
  border: 1px solid #333;
}

.path-section {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.path-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
  color: #ffffff;
  font-size: 0.9rem;
}

.path-label i {
  color: var(--brand-500);
  font-size: 1rem;
}

.path-display {
  background: #1e1e1e;
  border: 1px solid #333;
  border-radius: 6px;
  padding: 0.75rem;
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  font-size: 0.85rem;
  color: #cccccc;
  word-break: break-all;
  line-height: 1.4;
}

.path-display code {
  background: transparent;
  color: inherit;
  padding: 0;
  border: none;
  font-family: inherit;
}

.confirm-options {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.checkbox-row {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.75rem;
  background: #252526;
  border-radius: 8px;
  border: 1px solid #333;
  transition: all 0.2s ease;
}

.checkbox-row:hover {
  background: #2d2d30;
  border-color: var(--brand-500);
}

.checkbox-row label {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  cursor: pointer;
  width: 100%;
  margin: 0;
}

.checkbox-row input[type='checkbox'] {
  margin-top: 0.125rem;
  width: 1rem;
  height: 1rem;
  accent-color: var(--brand-500);
  cursor: pointer;
}

.checkbox-content {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  flex: 1;
}

.checkbox-title {
  font-weight: 500;
  color: #ffffff;
  font-size: 0.95rem;
}

.checkbox-content small {
  color: #aaaaaa;
  font-size: 0.8rem;
  line-height: 1.3;
}

.confirm-actions {
  display: flex;
  gap: 0.75rem;
  padding: 1rem 1.5rem 1.5rem;
  border-top: 1px solid #333;
  justify-content: flex-end;
  flex-wrap: wrap;
}

.confirm-actions .btn {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.75rem 1.25rem;
  border-radius: 6px;
  font-weight: 500;
  font-size: 0.9rem;
  transition: all 0.2s ease;
  border: 1px solid transparent;
  cursor: pointer;
  min-width: fit-content;
}

.confirm-actions .btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Local confirm action button variants are centralized in `src/assets/modals.css`. Use `.btn-primary` for confirm actions. */

/* Mobile responsive adjustments */
@media (max-width: 640px) {
  .confirm-overlay.separate-modal {
    padding: 0.5rem;
  }

  .confirm-dialog {
    max-width: 100%;
    margin: 0;
  }

  .confirm-header {
    padding: 1rem 1rem 0.75rem;
  }

  .confirm-header h3 {
    font-size: 1.1rem;
  }

  .confirm-body {
    padding: 1rem;
    gap: 1rem;
  }

  .path-comparison {
    padding: 0.75rem;
  }

  .path-display {
    padding: 0.5rem;
    font-size: 0.8rem;
  }

  .checkbox-row {
    padding: 0.5rem;
  }

  .confirm-actions {
    padding: 0.75rem 1rem 1rem;
    gap: 0.5rem;
  }

  .confirm-actions .btn {
    padding: 0.625rem 1rem;
    font-size: 0.85rem;
    flex: 1;
    justify-content: center;
  }
}
</style>
