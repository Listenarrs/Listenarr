<template>
  <Modal :visible="isOpen" size="lg" @close="close">
    <template #header>
      <ModalHeader
        title="Unmatched Files"
        :icon="PhMagnifyingGlass"
        @close="close"
      />
    </template>

    <template #default>
      <ModalBody>
        <!-- Scanning in progress -->
        <div v-if="phase === 'scanning'" class="scan-status">
          <PhSpinner class="ph-spin scan-spinner" />
          <p>Scanning <strong>{{ rootFolderName }}</strong> for audio files not in your library…</p>
        </div>

        <!-- Scan error -->
        <div v-else-if="phase === 'error'" class="scan-status error">
          <PhWarning class="error-icon" />
          <p>{{ errorMessage }}</p>
        </div>

        <!-- No scan yet -->
        <div v-else-if="phase === 'empty'" class="empty-state">
          <PhMagnifyingGlass class="empty-icon" />
          <h4>No scan results yet</h4>
          <p>Click <strong>Scan</strong> to search <strong>{{ rootFolderName }}</strong> for audio files not in your library.</p>
        </div>

        <!-- Results -->
        <div v-else-if="phase === 'results'">
          <div v-if="items.length === 0" class="empty-state">
            <PhCheckCircle class="empty-icon" />
            <h4>All files are in your library</h4>
            <p>No unmatched audio files were found in <strong>{{ rootFolderName }}</strong>.</p>
          </div>

          <div v-else>
            <p class="results-summary">
              Found <strong>{{ items.length }}</strong> folder{{ items.length !== 1 ? 's' : '' }} with audio files not in your library.
              <span v-if="lastScannedAt" class="last-scanned">Last scanned {{ timeAgo(lastScannedAt) }}</span>
            </p>

            <div class="results-table-wrapper">
              <table class="results-table">
                <thead>
                  <tr>
                    <th>Title</th>
                    <th>Author</th>
                    <th>Series</th>
                    <th>Year</th>
                    <th>Narrator</th>
                    <th>Files</th>
                    <th>Format</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="item in items" :key="item.fullPath" class="result-row">
                    <td class="cell-title" :title="item.description || undefined">
                      {{ item.title || item.relativePath }}
                    </td>
                    <td class="cell-author">{{ item.author || '—' }}</td>
                    <td class="cell-series">
                      <span v-if="item.series">{{ item.series }}<span v-if="item.seriesNumber" class="series-number"> #{{ item.seriesNumber }}</span></span>
                      <span v-else>—</span>
                    </td>
                    <td class="cell-year">{{ item.year || '—' }}</td>
                    <td class="cell-narrator">{{ item.narrator || '—' }}</td>
                    <td class="cell-files">{{ item.fileCount }}</td>
                    <td class="cell-format">{{ item.format }}</td>
                    <td class="cell-actions">
                      <button
                        class="btn btn-primary btn-sm"
                        @click="startAdd(item)"
                        title="Add to library"
                      >
                        Add
                      </button>
                      <button
                        class="icon-button btn-sm"
                        @click="ignore(item)"
                        title="Ignore"
                      >
                        <PhX />
                      </button>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <div v-if="phase === 'results' && rootFoldersStore.folders.length" class="destination-selector">
            <span class="dest-label">{{ fileActionLabel }}:</span>
            <select v-model="destinationFolderId" class="dest-select" :disabled="bulkAdding">
              <option v-for="f in rootFoldersStore.folders" :key="f.id" :value="f.id">{{ f.name }}</option>
            </select>
          </div>
          <div v-if="phase === 'results' && asinCount > 0 && !bulkAdding" class="bulk-hint">
            {{ asinCount }} item{{ asinCount !== 1 ? 's' : '' }} with ASIN
          </div>
          <div v-if="bulkAdding" class="bulk-progress">
            <PhSpinner class="ph-spin" />
            Adding {{ bulkDone }} / {{ bulkTotal }}…
          </div>
        </template>
        <button
          v-if="phase === 'results' && asinCount > 0"
          class="btn btn-primary"
          :disabled="bulkAdding"
          @click="addAllWithAsin"
          title="Search Audible metadata for every item with an ASIN and add matches automatically"
        >
          <PhRocketLaunch />
          Add All (ASIN match)
        </button>
        <button
          class="btn btn-secondary"
          :disabled="phase === 'scanning' || bulkAdding"
          @click="startScan"
          title="Scan for unmatched files"
        >
          <PhArrowsClockwise />
          Scan
        </button>
        <button class="btn" @click="close" :disabled="bulkAdding">Close</button>
      </ModalFooter>
    </template>
  </Modal>

  <!-- AddLibraryModal for confirming + adding an audiobook -->
  <AddLibraryModal
    v-if="addingItem"
    :visible="true"
    :book="addingBook"
    @close="addingItem = null"
    @added="onAdded"
  />
</template>

<script setup lang="ts">
import { ref, watch, computed, onUnmounted } from 'vue'
import Modal from '@/components/feedback/Modal.vue'
import ModalHeader from '@/components/feedback/ModalHeader.vue'
import ModalBody from '@/components/feedback/ModalBody.vue'
import ModalFooter from '@/components/feedback/ModalFooter.vue'
import AddLibraryModal from '@/components/domain/audiobook/AddLibraryModal.vue'
import {
  PhMagnifyingGlass,
  PhSpinner,
  PhWarning,
  PhCheckCircle,
  PhX,
  PhRocketLaunch,
  PhArrowsClockwise,
} from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { useToast } from '@/services/toastService'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useConfigurationStore } from '@/stores/configuration'
import type { UnmatchedFileItem, AudibleBookMetadata, Audiobook, RootFolder } from '@/types'

interface Props {
  isOpen: boolean
  rootFolder: RootFolder | null
}

interface Emits {
  (e: 'close'): void
}

const props = defineProps<Props>()
const emit = defineEmits<Emits>()

const toast = useToast()

type Phase = 'empty' | 'scanning' | 'results' | 'error'

const phase = ref<Phase>('empty')
const items = ref<UnmatchedFileItem[]>([])
const errorMessage = ref('')
const lastScannedAt = ref<string | null>(null)
const addingItem = ref<UnmatchedFileItem | null>(null)
const bulkAdding = ref(false)
const bulkDone = ref(0)
const bulkTotal = ref(0)

const asinCount = computed(() => items.value.filter((i) => i.asin).length)
const rootFolderName = computed(() => props.rootFolder?.name || props.rootFolder?.path || 'folder')

const rootFoldersStore = useRootFoldersStore()
const configStore = useConfigurationStore()

const destinationFolderId = ref<number | null>(null)

// Pre-select the default root folder once folders are available
watch(
  () => rootFoldersStore.folders,
  (folders) => {
    if (destinationFolderId.value === null && folders.length) {
      const target = folders.find((f) => f.isDefault) ?? folders[0]
      if (target) destinationFolderId.value = target.id
    }
  },
  { immediate: true },
)

const destinationFolder = computed(
  () => rootFoldersStore.folders.find((f) => f.id === destinationFolderId.value) ?? null,
)

const fileAction = computed(() => configStore.applicationSettings?.completedFileAction ?? 'Move')
const fileActionLabel = computed(() => (fileAction.value === 'Hardlink/Copy' ? 'Copy to' : 'Move to'))
const fileInputMode = computed<'move' | 'hardlink/copy'>(() =>
  fileAction.value === 'Hardlink/Copy' ? 'hardlink/copy' : 'move',
)

let jobId = ''
let offSignalR: (() => void) | null = null

// On open: load cached results — no auto-scan
watch(
  () => props.isOpen,
  async (open) => {
    if (!open) return
    if (!props.rootFolder?.id) return

    // Ensure stores are populated (may not be loaded yet if opened from a cold page)
    if (!configStore.applicationSettings) await configStore.loadApplicationSettings()
    if (!rootFoldersStore.folders.length) await rootFoldersStore.load()

    try {
      const saved = await apiService.getSavedUnmatchedFiles(props.rootFolder.id)
      if (saved.items.length > 0) {
        items.value = saved.items
        lastScannedAt.value = saved.lastScannedAt ?? null
        phase.value = 'results'
      } else {
        items.value = []
        lastScannedAt.value = null
        phase.value = 'empty'
      }
    } catch {
      items.value = []
      phase.value = 'empty'
    }
  },
)

// Explicit scan button handler
async function startScan() {
  if (!props.rootFolder?.id) return

  phase.value = 'scanning'
  items.value = []
  errorMessage.value = ''
  jobId = ''

  // Subscribe to SignalR before triggering the scan
  offSignalR?.()
  offSignalR = signalRService.onUnmatchedScanComplete(async (payload) => {
    if (payload.jobId !== jobId) return
    if (payload.error) {
      phase.value = 'error'
      errorMessage.value = payload.error
      return
    }
    try {
      const response = await apiService.getUnmatchedResults(payload.jobId)
      items.value = response.items
      lastScannedAt.value = new Date().toISOString()
      phase.value = 'results'
    } catch (e) {
      phase.value = 'error'
      errorMessage.value = (e as Error)?.message || 'Failed to fetch results'
    }
  })

  try {
    const result = await apiService.scanUnmatchedFiles(props.rootFolder.id)
    jobId = result.jobId
    // Poll once immediately — handles fast scans that complete before SignalR fires
    const check = await apiService.getUnmatchedResults(jobId)
    if (check.status === 'Completed') {
      items.value = check.items
      lastScannedAt.value = new Date().toISOString()
      phase.value = 'results'
    } else if (check.status === 'Failed') {
      phase.value = 'error'
      errorMessage.value = check.error || 'Scan failed'
    }
    // Otherwise SignalR will deliver the completion event
  } catch (e) {
    phase.value = 'error'
    errorMessage.value = (e as Error)?.message || 'Failed to start scan'
    offSignalR?.()
    offSignalR = null
  }
}

onUnmounted(() => {
  offSignalR?.()
})

function close() {
  offSignalR?.()
  offSignalR = null
  emit('close')
}

function timeAgo(isoString: string): string {
  const diff = Date.now() - new Date(isoString).getTime()
  const minutes = Math.floor(diff / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

// Build a minimal AudibleBookMetadata from path-parsed data to pre-fill AddLibraryModal
const addingBook = computed<AudibleBookMetadata>(() => {
  const item = addingItem.value
  if (!item) return { title: '', asin: '', authors: [] }
  return {
    title: item.title || item.relativePath.split('/').pop() || item.relativePath,
    asin: item.asin || '',
    authors: item.author ? [item.author] : [],
    series: item.series,
    seriesNumber: item.seriesNumber,
    publishYear: item.year,
    narrators: item.narrator ? [item.narrator] : [],
    description: item.description,
  }
})

function startAdd(item: UnmatchedFileItem) {
  addingItem.value = item
}

async function onAdded(audiobook: Audiobook) {
  if (!addingItem.value) return
  const item = addingItem.value
  addingItem.value = null

  // Link the file(s) to the newly added audiobook via manual-import
  try {
    await apiService.startManualImport({
      path: item.bookFolder,
      mode: 'interactive',
      action: fileInputMode.value,
      items: [
        {
          fullPath: item.fullPath,
          matchedAudiobookId: audiobook.id,
        },
      ],
    })
    toast.success('Added', `${audiobook.title || item.title || 'Book'} added to library`)
  } catch {
    toast.success('Added', `${audiobook.title || item.title || 'Book'} added — link files manually if needed`)
  }

  // Remove from the results table
  items.value = items.value.filter((i) => i.fullPath !== item.fullPath)
}

function ignore(item: UnmatchedFileItem) {
  items.value = items.value.filter((i) => i.fullPath !== item.fullPath)
}

// Minimal Audible response shape — only what we need for adding
interface AudiblePayload {
  asin?: string
  title?: string
  subtitle?: string
  publishDate?: string
  releaseDate?: string
  authors?: { name?: string }[]
  narrators?: { name?: string }[]
  series?: { title?: string; part?: string }[]
  description?: string
  imageUrl?: string
  lengthMinutes?: number
  language?: string
  genres?: { name?: string }[]
}

function mapToAudible(meta: AudiblePayload, fallback: UnmatchedFileItem): AudibleBookMetadata {
  const year = (meta.publishDate || meta.releaseDate || '').split(/[-/]/)[0] || fallback.year
  const firstSeries = meta.series?.[0]
  return {
    asin: meta.asin || fallback.asin || '',
    title: meta.title || fallback.title || '',
    subtitle: meta.subtitle,
    authors: (meta.authors || []).map((a) => a?.name).filter(Boolean) as string[],
    narrators: (meta.narrators || []).map((n) => n?.name).filter(Boolean) as string[],
    series: firstSeries?.title || fallback.series,
    seriesNumber: firstSeries?.part || fallback.seriesNumber,
    publishYear: year || undefined,
    description: meta.description || fallback.description,
    imageUrl: meta.imageUrl,
    runtime: typeof meta.lengthMinutes === 'number' ? meta.lengthMinutes * 60 : undefined,
    language: meta.language,
    genres: (meta.genres || []).map((g) => g?.name).filter(Boolean) as string[],
  }
}

async function addAllWithAsin() {
  const candidates = items.value.filter((i) => i.asin)
  if (!candidates.length) return

  bulkAdding.value = true
  bulkDone.value = 0
  bulkTotal.value = candidates.length
  let added = 0
  let skipped = 0

  for (const item of candidates) {
    try {
      // Fetch Audible-backed metadata for this ASIN
      const resp = await apiService.getAudibleMetadata<
        { source?: string; metadata?: AudiblePayload } | AudiblePayload
      >(item.asin!)

      // Unwrap response — may be { source, metadata } or direct Audible payload
      const raw =
        resp && 'metadata' in resp && resp.metadata ? resp.metadata : (resp as AudiblePayload)

      if (!raw?.title) {
        // No metamatch — skip, leave in table
        skipped++
        bulkDone.value++
        continue
      }

      const metadata = mapToAudible(raw, item)
      const { audiobook } = await apiService.addToLibrary(metadata, {
        destinationPath: destinationFolder.value?.path,
      })

      try {
        const importResult = await apiService.startManualImport({
          path: item.bookFolder,
          mode: 'interactive',
          action: fileInputMode.value,
          items: [{ fullPath: item.fullPath, matchedAudiobookId: audiobook.id }],
        })
        if (importResult && importResult.importedCount === 0) {
          toast.warning('File not linked', `${item.title || 'Book'} was added but the file could not be moved — check the library path`)
        }
      } catch {
        toast.warning('File not linked', `${item.title || 'Book'} was added but the file could not be moved`)
      }

      items.value = items.value.filter((i) => i.fullPath !== item.fullPath)
      added++
    } catch {
      skipped++
    }
    bulkDone.value++
  }

  bulkAdding.value = false

  if (added > 0) {
    toast.success('Bulk add complete', `Added ${added} book${added !== 1 ? 's' : ''}${skipped > 0 ? `, ${skipped} skipped (no match)` : ''}`)
  } else {
    toast.info('No matches found', 'No Audible metadata records found for the ASINs in this scan')
  }
}
</script>

<style scoped>
.scan-status {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 1rem;
  padding: 3rem;
  color: #adb5bd;
  text-align: center;
}

.scan-spinner {
  width: 40px;
  height: 40px;
  color: #4dabf7;
}

.scan-status.error {
  color: #f03e3e;
}

.error-icon {
  width: 40px;
  height: 40px;
}

.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.75rem;
  padding: 3rem;
  color: #adb5bd;
  text-align: center;
}

.empty-icon {
  width: 40px;
  height: 40px;
  color: #51cf66;
}

.empty-state h4 {
  margin: 0;
  color: #fff;
}

.results-summary {
  margin: 0 0 1rem;
  color: #adb5bd;
  font-size: 0.95rem;
}

.last-scanned {
  margin-left: 0.5rem;
  color: #868e96;
  font-size: 0.85rem;
}

.results-table-wrapper {
  overflow-x: auto;
}

.results-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.9rem;
}

.results-table th {
  text-align: left;
  padding: 0.5rem 0.75rem;
  color: #868e96;
  font-weight: 500;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  white-space: nowrap;
}

.results-table td {
  padding: 0.5rem 0.75rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.05);
  vertical-align: middle;
}

.result-row:hover td {
  background: rgba(255, 255, 255, 0.03);
}

.cell-title {
  max-width: 220px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: #fff;
  cursor: default;
}

.cell-author,
.cell-series,
.cell-narrator {
  max-width: 140px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.series-number {
  color: #868e96;
}

.cell-files,
.cell-year,
.cell-format {
  white-space: nowrap;
  color: #adb5bd;
}

.cell-actions {
  display: flex;
  gap: 0.4rem;
  align-items: center;
  white-space: nowrap;
}

.btn-sm {
  padding: 0.25rem 0.6rem;
  font-size: 0.85rem;
}

.bulk-hint {
  font-size: 0.85rem;
  color: #868e96;
}

.bulk-progress {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.9rem;
  color: #4dabf7;
}

.destination-selector {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.85rem;
}

.dest-label {
  color: #adb5bd;
  white-space: nowrap;
}

.dest-select {
  background: #2a2a2a;
  border: 1px solid #444;
  border-radius: 4px;
  color: #fff;
  font-size: 0.85rem;
  padding: 0.2rem 0.5rem;
  cursor: pointer;
}

.dest-select:disabled {
  opacity: 0.5;
  cursor: default;
}
</style>
