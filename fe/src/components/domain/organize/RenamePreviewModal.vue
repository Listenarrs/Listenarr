<template>
  <Modal :visible="visible" size="lg" @close="onClose">
    <template #header>
      <ModalHeader title="Organize Files" @close="onClose" :icon="PhFolderOpen" />
    </template>

    <template #default>
      <ModalBody>
        <!-- Loading state -->
        <div v-if="loading" class="organize-loading">
          <PhSpinner class="ph-spin" style="width:24px;height:24px" />
          <span>Computing expected paths…</span>
        </div>

        <!-- No changes needed -->
        <div v-else-if="loaded && changedPreviews.length === 0 && !executing" class="organize-empty">
          <PhCheck class="check-icon" />
          <p class="organize-empty-title">All files are already properly named.</p>
          <p class="organize-hint">Current naming pattern: <code>{{ namingHint }}</code></p>
        </div>

        <!-- Execution progress -->
        <div v-else-if="executing || finished" class="organize-progress">
          <div v-if="executing" class="progress-header">
            <PhSpinner class="ph-spin" style="width:20px;height:20px" />
            <span>Organizing {{ progressCurrent }} of {{ progressTotal }}…</span>
          </div>
          <div v-else class="progress-header progress-done">
            <PhCheck class="check-icon" style="width:28px;height:28px" />
            <span>Organize complete</span>
          </div>
          <div v-if="executing" class="progress-bar-track">
            <div class="progress-bar-fill" :style="{ width: progressPercent + '%' }"></div>
          </div>

          <!-- While executing: show live per-audiobook log -->
          <div v-if="executing" class="progress-log">
            <div
              v-for="entry in progressLog"
              :key="entry.audiobookId"
              class="log-entry"
              :class="{ 'log-ok': entry.success, 'log-fail': entry.success === false, 'log-pending': entry.success === null }"
            >
              <PhCheck v-if="entry.success === true" class="log-icon" />
              <PhWarning v-else-if="entry.success === false" class="log-icon" />
              <PhSpinner v-else class="log-icon ph-spin" />
              <span class="log-title">{{ entry.title }}</span>
              <span v-if="entry.detail" class="log-detail">{{ entry.detail }}</span>
            </div>
          </div>

          <!-- After completion: table view with failures first -->
          <div v-if="finished" class="organize-complete">
            <div class="complete-summary-bar">
              <span v-if="successCount > 0" class="summary-pill pill-ok">
                <PhCheck :size="14" /> {{ successCount }} organized
              </span>
              <span v-if="failedResults.length > 0" class="summary-pill pill-fail">
                <PhWarning :size="14" /> {{ failedResults.length }} failed
              </span>
            </div>

            <div class="results-table-wrap">
              <table class="results-table">
                <thead>
                  <tr>
                    <th class="col-status"></th>
                    <th class="col-title">Audiobook</th>
                    <th class="col-detail">Details</th>
                  </tr>
                </thead>
                <tbody>
                  <!-- Failures first -->
                  <tr v-for="r in failedResults" :key="'fail-' + r.audiobookId" class="row-fail">
                    <td class="col-status"><PhWarning class="status-icon icon-fail" /></td>
                    <td class="col-title">{{ getAudiobookTitle(r.audiobookId) }}</td>
                    <td class="col-detail text-fail">{{ r.error }}</td>
                  </tr>
                  <!-- Successes -->
                  <tr v-for="entry in progressLog.filter(e => e.success === true)" :key="'ok-' + entry.audiobookId" class="row-ok">
                    <td class="col-status"><PhCheck class="status-icon icon-ok" /></td>
                    <td class="col-title">{{ entry.title }}</td>
                    <td class="col-detail">{{ entry.detail }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
        </div>

        <!-- Preview list -->
        <div v-else-if="loaded" class="organize-preview">
          <p class="organize-hint">
            Naming pattern: <code>{{ namingHint }}</code>
          </p>
          <div class="organize-toolbar">
            <p class="organize-summary">
              {{ selectedCount }} of {{ changedPreviews.length }} audiobook(s) selected
              <template v-if="totalFolderChanges > 0">· {{ totalFolderChanges }} folder(s) to move</template>
              <template v-if="totalFileChanges > 0">· {{ totalFileChanges }} file(s) to rename</template>
            </p>
            <div class="organize-actions">
              <button class="btn-link" @click="selectAllChanged">Select All</button>
              <button class="btn-link" @click="deselectAll">Deselect All</button>
              <button v-if="!showAllPreviews && changedPreviews.length > previewLimit" class="btn-link" @click="showAllPreviews = true">
                Show all ({{ changedPreviews.length }})
              </button>
            </div>
          </div>

          <div
            v-for="preview in visiblePreviews"
            :key="preview.audiobookId"
            class="preview-item"
            :class="{ 'preview-expanded': expanded.has(preview.audiobookId) }"
          >
            <div class="preview-header">
              <input
                type="checkbox"
                :checked="selected.has(preview.audiobookId)"
                @change="toggleSelected(preview.audiobookId)"
              />
              <span class="preview-title" @click="toggleExpanded(preview.audiobookId)">
                {{ preview.audiobookTitle || `Audiobook #${preview.audiobookId}` }}
              </span>
              <span class="preview-badge">
                <template v-if="preview.folderChanged">folder</template>
                <template v-if="preview.folderChanged && fileChangeCount(preview) > 0"> + </template>
                <template v-if="fileChangeCount(preview) > 0">{{ fileChangeCount(preview) }} file{{ fileChangeCount(preview) > 1 ? 's' : '' }}</template>
              </span>
              <button class="expand-toggle" @click="toggleExpanded(preview.audiobookId)" :title="expanded.has(preview.audiobookId) ? 'Collapse' : 'Expand'">
                {{ expanded.has(preview.audiobookId) ? '▾' : '▸' }}
              </button>
            </div>

            <!-- Expanded details -->
            <template v-if="expanded.has(preview.audiobookId)">
              <!-- Folder change -->
              <div v-if="preview.folderChanged" class="preview-change folder-change">
                <span class="change-label">Folder</span>
                <RenamePathDiff :old-path="preview.currentFolderPath" :new-path="preview.newFolderPath" />
              </div>

              <!-- File changes -->
              <div
                v-for="file in preview.fileRenames.filter(f => f.changed)"
                :key="file.fileId"
                class="preview-change file-change"
              >
                <span class="change-label">File</span>
                <RenamePathDiff :old-path="file.currentFilename" :new-path="file.newFilename" />
              </div>
            </template>
          </div>

          <div v-if="!showAllPreviews && changedPreviews.length > previewLimit" class="show-more">
            <button class="btn-link" @click="showAllPreviews = true">
              Show {{ changedPreviews.length - previewLimit }} more audiobook(s)…
            </button>
          </div>
        </div>

        <!-- Error state -->
        <div v-if="error" class="organize-error">
          <PhWarning class="warning-icon" />
          <span>{{ error }}</span>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button class="btn cancel-button" @click="onClose">
            <PhX :size="16" /> {{ finished ? 'Close' : 'Cancel' }}
          </button>
        </template>
        <template #default>
          <button
            v-if="!finished"
            class="btn btn-primary"
            :disabled="executing || selectedCount === 0"
            @click="onConfirm"
          >
            <PhSpinner v-if="executing" class="ph-spin" style="width:16px;height:16px" />
            <PhFolderOpen v-else :size="16" />
            {{ executing ? 'Organizing…' : `Organize ${selectedCount} Audiobook(s)` }}
          </button>
          <button
            v-else
            class="btn btn-primary"
            @click="onFinishedClose"
          >
            <PhCheck :size="16" />
            Done
          </button>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { PhFolderOpen, PhSpinner, PhCheck, PhWarning, PhX } from '@phosphor-icons/vue'
import Modal from '@/components/feedback/Modal.vue'
import ModalHeader from '@/components/feedback/ModalHeader.vue'
import ModalBody from '@/components/feedback/ModalBody.vue'
import ModalFooter from '@/components/feedback/ModalFooter.vue'
import RenamePathDiff from './RenamePathDiff.vue'
import { apiService } from '@/services/api'
import type { RenamePreview, RenameOperation, RenameResult } from '@/types'

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

// State
const loading = ref(false)
const loaded = ref(false)
const executing = ref(false)
const finished = ref(false)
const error = ref<string | null>(null)
const previews = ref<RenamePreview[]>([])
const results = ref<RenameResult[]>([])
const selected = ref<Set<number>>(new Set())
const progressCurrent = ref(0)
const progressTotal = ref(0)
const progressCurrentTitle = ref<string | null>(null)

interface ProgressLogEntry {
  audiobookId: number
  title: string
  success: boolean | null // null = in progress
  detail?: string
}
const progressLog = ref<ProgressLogEntry[]>([])
const expanded = ref<Set<number>>(new Set())
const showAllPreviews = ref(false)
const previewLimit = 20
// Computed
const changedPreviews = computed(() => previews.value.filter((p) => p.hasChanges))

const selectedCount = computed(() => {
  return changedPreviews.value.filter((p) => selected.value.has(p.audiobookId)).length
})

const totalFileChanges = computed(() => {
  return changedPreviews.value
    .filter((p) => selected.value.has(p.audiobookId))
    .reduce((sum, p) => sum + p.fileRenames.filter((f) => f.changed && f.currentFilename !== f.newFilename).length, 0)
})

const totalFolderChanges = computed(() => {
  return changedPreviews.value
    .filter((p) => selected.value.has(p.audiobookId) && p.folderChanged)
    .length
})

const namingHint = computed(() => {
  // Show a representative pattern — we don't have the settings here,
  // but we can infer from the first preview's new folder path
  const first = changedPreviews.value[0]
  if (first?.newFolderPath) return first.newFolderPath
  return '{Author}/{Series}/{Title}'
})

const progressPercent = computed(() => {
  if (progressTotal.value === 0) return 0
  return Math.round((progressCurrent.value / progressTotal.value) * 100)
})

const successCount = computed(() => results.value.filter((r) => r.success).length)
const failedResults = computed(() => results.value.filter((r) => !r.success))

function getAudiobookTitle(audiobookId: number): string {
  const preview = previews.value.find((p) => p.audiobookId === audiobookId)
  return preview?.audiobookTitle || `Audiobook #${audiobookId}`
}

const visiblePreviews = computed(() => {
  if (showAllPreviews.value) return changedPreviews.value
  return changedPreviews.value.slice(0, previewLimit)
})

function fileChangeCount(preview: RenamePreview): number {
  return preview.fileRenames.filter((f) => f.changed && f.currentFilename !== f.newFilename).length
}

function toggleExpanded(id: number) {
  const next = new Set(expanded.value)
  if (next.has(id)) {
    next.delete(id)
  } else {
    next.add(id)
  }
  expanded.value = next
}

function selectAllChanged() {
  selected.value = new Set(changedPreviews.value.map((p) => p.audiobookId))
}

function deselectAll() {
  selected.value = new Set()
}

// Load previews when modal opens
watch(
  () => props.visible,
  async (isVisible) => {
    if (isVisible && props.audiobookIds.length > 0) {
      await loadPreviews()
    } else if (!isVisible) {
      resetState()
    }
  },
)

async function loadPreviews() {
  loading.value = true
  loaded.value = false
  error.value = null
  results.value = []

  try {
    previews.value = await apiService.previewRename(props.audiobookIds)
    // Auto-select all changed audiobooks
    selected.value = new Set(changedPreviews.value.map((p) => p.audiobookId))
    loaded.value = true
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : 'Failed to load rename previews'
  } finally {
    loading.value = false
  }
}

async function onConfirm() {
  if (selectedCount.value === 0) return

  executing.value = true
  finished.value = false
  error.value = null
  results.value = []
  progressLog.value = []

  const selectedPreviews = changedPreviews.value.filter((p) => selected.value.has(p.audiobookId))
  progressTotal.value = selectedPreviews.length
  progressCurrent.value = 0
  progressCurrentTitle.value = null

  // Pre-fill log with pending entries
  progressLog.value = selectedPreviews.map((p) => ({
    audiobookId: p.audiobookId,
    title: p.audiobookTitle || `Audiobook #${p.audiobookId}`,
    success: null,
  }))

  for (let i = 0; i < selectedPreviews.length; i++) {
    const p = selectedPreviews[i]!
    progressCurrent.value = i + 1
    progressCurrentTitle.value = p.audiobookTitle || `Audiobook #${p.audiobookId}`

    // Mark current as in-progress
    const logEntry = progressLog.value[i]!

    try {
      const operation: RenameOperation = {
        audiobookId: p.audiobookId,
        newFolderPath: p.folderChanged ? p.newFolderPath : undefined,
        fileRenames: p.fileRenames
          .filter((f) => f.changed)
          .map((f) => ({
            fileId: f.fileId,
            currentPath: f.currentPath ?? '',
            newPath: f.newPath ?? '',
          })),
      }

      const batchResults = await apiService.executeRename([operation])
      results.value.push(...batchResults)

      const r = batchResults[0]
      if (r && r.success) {
        logEntry.success = true
        const fileCount = r.renamedFiles?.filter((f) => f.success).length ?? 0
        const parts: string[] = []
        if (p.folderChanged) parts.push('folder moved')
        if (fileCount > 0) parts.push(`${fileCount} file(s) renamed`)
        if (parts.length === 0) parts.push('path updated')
        logEntry.detail = parts.join(', ')
      } else {
        logEntry.success = false
        logEntry.detail = r?.error || 'Unknown error'
      }
    } catch (e: unknown) {
      logEntry.success = false
      logEntry.detail = e instanceof Error ? e.message : 'Request failed'
      results.value.push({
        audiobookId: p.audiobookId,
        success: false,
        error: logEntry.detail,
        renamedFiles: [],
      })
    }
  }

  executing.value = false
  finished.value = true
  progressCurrentTitle.value = null
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

function onClose() {
  if (finished.value) {
    emit('done')
  } else {
    emit('close')
  }
}

function onFinishedClose() {
  emit('done')
}

function resetState() {
  loading.value = false
  loaded.value = false
  executing.value = false
  finished.value = false
  error.value = null
  previews.value = []
  results.value = []
  selected.value = new Set()
  expanded.value = new Set()
  showAllPreviews.value = false
  progressCurrent.value = 0
  progressTotal.value = 0
  progressCurrentTitle.value = null
  progressLog.value = []
}
</script>

<style scoped>
.organize-loading,
.organize-empty,
.organize-progress {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 2rem;
  gap: 0.75rem;
}

.organize-empty-title {
  color: #e6eef8;
  font-size: 1.1rem;
  font-weight: 500;
  margin: 0;
}

.check-icon {
  color: var(--text-success, #66bb6a);
  width: 32px;
  height: 32px;
}

.organize-hint {
  font-size: 0.85rem;
  color: #bfc8cc;
  margin-bottom: 0.5rem;
}

.organize-hint code {
  background: #1f1f1f;
  padding: 0.2rem 0.5rem;
  border-radius: 4px;
  font-size: 0.85rem;
  color: #e6eef8;
}

.organize-summary {
  font-size: 0.9rem;
  color: #ddd;
  margin: 0;
}

.organize-toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.75rem;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.organize-actions {
  display: flex;
  gap: 0.75rem;
}

.btn-link {
  background: none;
  border: none;
  color: var(--brand-500, #4dabf7);
  cursor: pointer;
  font-size: 0.8rem;
  padding: 0;
}

.btn-link:hover {
  text-decoration: underline;
  color: var(--brand-300, #74c0fc);
}

.show-more {
  text-align: center;
  padding: 0.75rem;
}

.organize-preview {
  max-height: 60vh;
  overflow-y: auto;
  padding-right: 0.5rem;
}

.preview-item {
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  padding: 0.5rem 0.75rem;
  margin-bottom: 0.5rem;
}

.preview-header {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.9rem;
  color: #e6eef8;
}

.preview-header input[type='checkbox'] {
  accent-color: var(--brand-500, #2196f3);
  width: 16px;
  height: 16px;
}

.preview-title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 500;
  cursor: pointer;
  flex: 1;
}

.preview-title:hover {
  color: #4dabf7;
}

.preview-badge {
  font-size: 0.75rem;
  color: #868e96;
  white-space: nowrap;
  padding: 0.15rem 0.4rem;
  background: rgba(255, 255, 255, 0.04);
  border-radius: 4px;
}

.expand-toggle {
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.2rem 0.4rem;
  font-size: 0.85rem;
  border-radius: 4px;
}

.expand-toggle:hover {
  background: rgba(255, 255, 255, 0.06);
  color: #ced4da;
}

.preview-change {
  margin-top: 0.4rem;
  margin-left: 1.5rem;
  padding: 0.4rem 0.6rem;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.02);
}

.change-label {
  display: inline-block;
  width: 50px;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  color: #868e96;
}

.folder-change {
  border-left: 3px solid #42a5f5;
}

.file-change {
  border-left: 3px solid #66bb6a;
}

.organize-error {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.75rem;
  margin-top: 0.75rem;
  background: rgba(255, 82, 82, 0.1);
  border: 1px solid rgba(255, 82, 82, 0.3);
  border-radius: 8px;
  color: #ef5350;
  font-size: 0.85rem;
}

.warning-icon {
  flex-shrink: 0;
  width: 20px;
  height: 20px;
}

.organize-results {
  margin-top: 0.75rem;
}

.result-summary {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.6rem 0.8rem;
  border-radius: 6px;
  font-size: 0.9rem;
  margin-bottom: 0.5rem;
}

.result-item {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.4rem 0.6rem;
  border-radius: 4px;
  font-size: 0.85rem;
  margin-bottom: 0.25rem;
}

.result-ok {
  color: var(--text-success, #66bb6a);
  background: rgba(102, 187, 106, 0.08);
}

.result-fail {
  color: var(--text-danger, #ef5350);
  background: rgba(239, 83, 80, 0.08);
}

.result-icon {
  width: 16px;
  height: 16px;
  flex-shrink: 0;
}

.cancel-button {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.progress-header {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.95rem;
  color: #e6eef8;
}

.progress-bar-track {
  width: 100%;
  max-width: 400px;
  height: 8px;
  background: rgba(255, 255, 255, 0.08);
  border-radius: 4px;
  overflow: hidden;
}

.progress-bar-fill {
  height: 100%;
  background: var(--brand-500, #2196f3);
  border-radius: 4px;
  transition: width 0.3s ease;
}

.progress-current-title {
  font-size: 0.85rem;
  color: #868e96;
  margin: 0;
}

.progress-done {
  color: #66bb6a;
}

.organize-complete {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  margin-top: 0.5rem;
}

.complete-summary-bar {
  display: flex;
  gap: 0.75rem;
  justify-content: center;
}

.summary-pill {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.4rem 0.85rem;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
}

.pill-ok {
  background: rgba(102, 187, 106, 0.12);
  color: #66bb6a;
  border: 1px solid rgba(102, 187, 106, 0.25);
}

.pill-fail {
  background: rgba(239, 83, 80, 0.12);
  color: #ef5350;
  border: 1px solid rgba(239, 83, 80, 0.25);
}

.results-table-wrap {
  max-height: 50vh;
  overflow-y: auto;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 8px;
}

.results-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.85rem;
}

.results-table thead {
  position: sticky;
  top: 0;
  background: #2a2a2a;
  z-index: 1;
}

.results-table th {
  text-align: left;
  padding: 0.5rem 0.75rem;
  color: #868e96;
  font-weight: 500;
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.results-table td {
  padding: 0.45rem 0.75rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  color: #ddd;
}

.results-table .col-status {
  width: 32px;
  text-align: center;
}

.results-table .col-title {
  font-weight: 500;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 250px;
}

.results-table .col-detail {
  color: #bfc8cc;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 300px;
}

.status-icon {
  width: 16px;
  height: 16px;
}

.icon-ok { color: #66bb6a; }
.icon-fail { color: #ef5350; }
.text-fail { color: #ef5350; }

.row-fail {
  background: rgba(239, 83, 80, 0.04);
}

.row-ok:hover,
.row-fail:hover {
  background: rgba(255, 255, 255, 0.03);
}

.progress-log {
  width: 100%;
  max-width: 500px;
  max-height: 40vh;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  margin-top: 0.5rem;
}

.log-entry {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.35rem 0.5rem;
  border-radius: 4px;
  font-size: 0.85rem;
}

.log-ok {
  color: var(--text-success, #66bb6a);
}

.log-fail {
  color: var(--text-danger, #ef5350);
  background: rgba(239, 83, 80, 0.06);
}

.log-pending {
  color: var(--text-muted, #868e96);
}

.log-icon {
  flex-shrink: 0;
  width: 16px;
  height: 16px;
}

.log-title {
  font-weight: 500;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 200px;
}

.log-detail {
  color: #868e96;
  font-size: 0.8rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
