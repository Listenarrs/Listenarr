<template>
  <Modal :visible="isOpen" size="lg" @close="close">
    <template #header>
      <ModalHeader :title="'Manual Import - Select Folder'" :icon="PhFolderOpen" @close="close" />
    </template>

    <template #default>
      <ModalBody :class="{ 'browser-mode': browserMode }">
        <!-- Recent folders (session storage) -->
        <div v-if="!showPreview && recentFolders.length > 0" class="recent-folders">
          <div class="recent-title">Recent folders</div>
          <div class="recent-list">
            <button
              v-for="p in recentFolders"
              :key="p"
              class="recent-item"
              @click="selectRecent(p)"
            >
              {{ p }}
            </button>
          </div>
        </div>

        <!-- Top folder input (full width) - hidden when preview is active -->
        <div v-if="!showPreview" class="top-path">
          <div class="top-path-row">
            <FolderBrowser
              v-model="selectedPath"
              :inline="true"
              :show-files="true"
              :auto-browse="false"
              @browser-opened="browserMode = true"
              @browser-closed="browserMode = false"
              @open-modal="showBrowserModal = true"
            />
          </div>
        </div>

        <!-- Centered action buttons - shown when valid path exists -->
        <div v-if="!showPreview && isPathValid && !browserMode" class="center-actions">
          <button
            class="btn btn-info"
            @click="startAutomaticImport"
            :disabled="!isPathValid || loading"
          >
            <PhRocket />
            Automatic Import
          </button>
          <button
            class="btn btn-primary"
            @click="startInteractiveImport"
            :disabled="!isPathValid || loading"
          >
            <PhUser />
            Interactive Import
          </button>
        </div>

        <!-- Preview area (hidden until Interactive Import is clicked) -->
        <div v-if="showPreview" class="preview-area">
          <div v-if="loading" class="loading-state">
            <PhSpinner class="ph-spin" />
            Loading files...
          </div>

          <div v-else-if="previewItems.length > 0" class="preview-list">
            <div class="preview-table">
              <div class="preview-header">
                <div class="col col-check">
                  <Checkbox :modelValue="allSelected" @update:modelValue="setAllSelected" />
                </div>
                <div v-for="(field, _) in importFields" :class="`col ${ field.class }`">{{ field.label }}</div>
                <div class="col col-action"></div>
              </div>

              <div class="preview-body">
                <div v-for="(it, idx) in previewItems" :key="idx" class="preview-row">
                  <div class="col col-check"><Checkbox v-model="it.selected" /></div>
                  <div v-for="(field, _) in importFields" :class="`col ${ field.class }`">
                    <div v-if="field.editable" class="clickable-cell" @click="openCellEditor(it, field.key)">
                      <span v-if="field.get(it)">{{
                        field.display(it)
                      }}</span>
                      <span v-else class="placeholder">&nbsp;</span>
                    </div>
                    <span v-else>{{ field.display(it) }}</span>
                  </div>
                  <div class="col col-action">
                    <div
                      v-if="getItemIssues(it).length > 0"
                      class="info-icon"
                      :title="getItemIssues(it).join(', ')"
                    >
                      <PhInfo />
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div v-else class="preview-empty">
            <p>No files found in the selected folder.</p>
          </div>
        </div>
      </ModalBody>

    </template>

    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button class="cancel-button btn" @click="close"><PhX /> Cancel</button>
        </template>

        <template #default>
          <select
            v-if="showPreview"
            v-model="inputField"
            class="extra-select"
            @change="openCellsEditor()"
            :disabled="selectedCount === 0 || loading"
          >
            <option value="">Select...</option>
            <option v-for="(field, _) in importFields.filter((field: ImportField) => field.editable)" :value="field.key">{{ field.label }}</option>
          </select>

          <select v-if="showPreview" class="extra-select" v-model="inputMode">
            <option value="">Select Import Mode</option>
            <option value="move">Move</option>
            <option value="hardlink/copy">Hardlink/Copy</option>
          </select>

          <!-- Show Interactive/Automatic Import when browser is open and not in preview mode -->
          <template v-if="!showPreview && browserMode">
            <button
              class="btn btn-info"
              @click="startAutomaticImport"
              :disabled="!isPathValid || loading"
            >
              <PhRocket />
              Automatic Import
            </button>
            <button
              class="btn btn-primary"
              @click="startInteractiveImport"
              :disabled="!isPathValid || loading"
            >
              <PhUser />
              Interactive Import
            </button>
          </template>

          <!-- Show Import button in preview mode -->
          <button
            v-else-if="showPreview"
            class="btn btn-primary"
            @click="importSelected"
            :disabled="selectedCount === 0 || loading"
          >
            Import
          </button>
        </template>
      </ModalFooter>
    </template>
  </Modal>

  <FolderBrowserModal v-model:visible="showBrowserModal" v-model:modelValue="selectedPath" :show-input="true" :show-files="true" @close="showBrowserModal = false" />

    <Modal :visible="showMatch" size="lg" @close="closeMatch">
      <template #header>
        <h3>Match file to audiobook</h3>
      </template>

      <template #default>
        <select v-model="matchSelection" class="form-select">
          <option v-for="book in library" :key="book.id" :value="book.id">{{ getBookDisplay(book) }}</option>
        </select>
      </template>

      <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button class="cancel-button btn" @click="closeMatch"><PhX /> Cancel</button>
        </template>
        <template #default>
          <button class="btn btn-primary" @click="confirmMatch">Match</button>
        </template>
      </ModalFooter>
    </template>
    </Modal>

  <Modal :visible="showCellEditor" size="lg" @close="closeCellEditor">
    <template #header>
      <ModalHeader :title="'Edit'" @close="closeCellEditor" />
    </template>

    <template #default>
      <div>
        <div v-if="cellEditorField === 'audiobook'" class="editor-row">
          <div class="audiobook-table">
            <div class="table-header">
              <div class="table-col col-audiobook">Audiobook</div>
              <div class="table-col col-author">Author</div>
              <div class="table-col col-year">Year</div>
              <div class="table-col col-asin">ASIN</div>
            </div>
            <div class="table-body">
              <div
                v-for="book in library"
                :key="book.id"
                class="table-row"
                :class="{ active: cellEditorValue === book.id }"
                @click="selectEditorChoice(book.id)"
              >
                <div class="table-col col-audiobook">{{ book.title || 'Unknown' }}</div>
                <div class="table-col col-author">{{ getBookAuthor(book) }}</div>
                <div class="table-col col-year">{{ getBookYear(book) }}</div>
                <div class="table-col col-asin">{{ getBookAsin(book) }}</div>
              </div>
            </div>
          </div>
        </div>

        <div v-else-if="cellEditorField === 'quality'" class="editor-row">
          <div class="audiobook-table">
            <div class="table-header">
              <div class="table-col col-quality-profile">Quality Profile</div>
              <div class="table-col col-quality-description">Description</div>
            </div>
            <div class="table-body">
              <div
                v-for="q in qualityProfiles"
                :key="q.id"
                class="table-row"
                :class="{ active: cellEditorValue == q.id }"
                @click="selectEditorChoice(q.id ?? null)"
              >
                <div class="table-col col-quality-profile">{{ q.name }}</div>
                <div class="table-col col-quality-description">{{ q.description || '' }}</div>
              </div>
            </div>
          </div>
        </div>

        <div v-else-if="cellEditorField === 'language'" class="editor-row">
          <div class="audiobook-table">
            <div class="table-header">
              <div class="table-col col-language-name">Language</div>
            </div>
            <div class="table-body">
              <div
                v-for="(name, code) in languageMap"
                :key="code"
                class="table-row"
                :class="{ active: cellEditorValue === code }"
                @click="selectEditorChoice(code)"
              >
                <div class="table-col col-language-name">{{ name }}</div>
              </div>
            </div>
          </div>
        </div>

        <div v-else-if="cellEditorField === 'releaseGroup'" class="editor-row">
          <label>Release Group</label>
          <input v-model="cellEditorValue" class="form-input" placeholder="Enter release group..." />
        </div>
      </div>
    </template>

    <template #footer>
      <button class="cancel-button btn" @click="closeCellEditor">Cancel</button>
      <button v-if="cellEditorField === 'releaseGroup'" class="btn btn-primary" @click="saveCellEditor">Save</button>
    </template>
  </Modal> 
</template>

<script setup lang="ts">
import { ref, watch, computed, onMounted } from 'vue'
import { PhFolderOpen, PhX, PhRocket, PhUser, PhSpinner, PhInfo } from '@phosphor-icons/vue'
import type { ManualImportRequest } from '@/types'
import FolderBrowser from '@/components/ui/FolderBrowser.vue'
import Checkbox from '@/components/form/Checkbox.vue'
import FolderBrowserModal from '@/components/feedback/FolderBrowserModal.vue' 
import { Modal, ModalHeader, ModalBody, ModalFooter } from '@/components/feedback' /* keep ModalFooter for footer layout */
import { apiService } from '@/services/api'
import { useLibraryStore } from '@/stores/library'
import { useConfigurationStore } from '@/stores/configuration'

const props = withDefaults(defineProps<{ isOpen?: boolean; initialPath?: string }>(), {
  isOpen: false,
  initialPath: '',
})

const emit = defineEmits(['close', 'imported'] as const)

const selectedPath = ref(props.initialPath || '')
const loading = ref(false)
const browserMode = ref(false)
const inputField = ref<string>('')
const inputMode = ref<'move' | 'hardlink/copy' | ''>('')

const showPreview = ref(false)
interface PreviewItem {
  relativePath: string
  audiobook?: string
  quality?: string
  languages?: string[]
  size?: string | null
  selected?: boolean
  matchedAudiobookId?: number | null
  releaseGroup?: string | null
  qualityProfileId?: number | null
  language?: string | null
  fullPath?: string | null
  rejections?: string[]
}
const previewItems = ref<PreviewItem[]>([])

interface ImportField {
  key: string;
  label: string;
  class: string;
  editable: boolean;
  set: (item: PreviewItem, value: any) => void;
  get: (item: PreviewItem) => any;
  display: (item: PreviewItem) => string;
}
const importFields = [
  {
    key: 'path',
    label: 'Relative Path',
    class: 'col-path relative',
    editable: false,
    get: (item: PreviewItem) => item.relativePath,
    display: (item: PreviewItem) => item.relativePath
  },
  {
    key: 'audiobook',
    label: 'Audiobook',
    class: 'col-audiobook',
    editable: true,
    set: (item: PreviewItem, value: number) => item.matchedAudiobookId = value,
    get: (item: PreviewItem) => item.matchedAudiobookId,
    display: (item: PreviewItem) => getLibraryTitle(item.matchedAudiobookId)
  },
  {
    key: 'releaseGroup',
    label: 'Release Group',
    class: 'col-release-group',
    editable: true,
    set: (item: PreviewItem, value: string) => item.releaseGroup = value,
    get: (item: PreviewItem) => item.releaseGroup,
    display: (item: PreviewItem) => item.releaseGroup
  },
  {
    key: 'quality',
    label: 'Quality',
    class: 'col-quality',
    editable: true,
    set: (item: PreviewItem, value: number) => item.qualityProfileId = value,
    get: (item: PreviewItem) => item.qualityProfileId,
    display: (item: PreviewItem) => getQualityName(item.qualityProfileId)
  },
  {
    key: 'language',
    label: 'Language',
    class: 'col-language',
    editable: true,
    set: (item: PreviewItem, value: string) => item.language = value,
    get: (item: PreviewItem) => item.language,
    display: (item: PreviewItem) => getLanguageName(item.language)
  },
  {
    key: 'size',
    label: 'Size',
    class: 'col-size',
    editable: false,
    get: (item: PreviewItem) => item.size,
    display: (item: PreviewItem) => item.size
  }
] as ImportField[]

// Recent folders stored in sessionStorage
const RECENT_KEY = 'manualImport.recentFolders'
const recentFolders = ref<string[]>([])

const loadRecentFolders = () => {
  try {
    const raw = sessionStorage.getItem(RECENT_KEY)
    if (!raw) return (recentFolders.value = [])
    const arr = JSON.parse(raw) as string[]
    recentFolders.value = Array.isArray(arr) ? arr : []
  } catch {
    recentFolders.value = []
  }
}

const saveRecentFolder = (path: string) => {
  if (!path) return
  // keep most recent first, dedupe, cap at 10
  const arr = [path, ...recentFolders.value.filter((p) => p !== path)].slice(0, 10)
  recentFolders.value = arr
  try {
    sessionStorage.setItem(RECENT_KEY, JSON.stringify(arr))
  } catch {}
}

const selectRecent = (path: string) => {
  selectedPath.value = path
  // Validation will be triggered automatically by FolderBrowser's watcher
}
const libraryStore = useLibraryStore()
const library = computed(() => libraryStore.audiobooks)
const configurationStore = useConfigurationStore()
const qualityProfiles = computed(() => configurationStore.qualityProfiles)
const showMatch = ref(false)
const matchTarget = ref<PreviewItem | null>(null)
const matchSelection = ref<number | null>(null)

// Cell editor state: used when clicking table cells to edit audiobook/quality/lang/release-group
const showCellEditor = ref(false)
const cellEditorItems = ref<PreviewItem[]>([])
const cellEditorField = ref<string | null>(null)
const cellEditorValue = ref<number | string | null>(null)

// Folder browser modal state for explicit Browse button
const showBrowserModal = ref(false)

// Helper display names
const getLibraryTitle = (id?: number | null) => {
  if (!id) return ''
  const found = library.value.find((b) => b.id === id)
  return found ? found.title || 'Unknown' : String(id)
}

type Book = {
  id?: number
  title?: string
  authors?: string[]
  year?: number | string
  publishYear?: number | string
  releaseDate?: string
  asin?: string
  asin13?: string
}

const getBookDisplay = (book: Book) => {
  const title = book.title ?? 'Untitled'
  const author = book.authors && book.authors.length > 0 ? book.authors[0] : ''
  const yearCandidate =
    book.year ??
    book.publishYear ??
    (book.releaseDate ? String(book.releaseDate).slice(0, 4) : undefined)
  const year = yearCandidate ? Number(String(yearCandidate)) : undefined
  const asin = book.asin ?? book.asin13 ?? undefined
  const meta: string[] = []
  if (author) meta.push(author)
  if (!Number.isNaN(year) && year) meta.push(String(year))
  if (asin) meta.push(`ASIN: ${asin}`)
  return meta.length ? `${title} — ${meta.join(' • ')}` : title
}

const getBookAuthor = (book: Book) => {
  return book.authors && book.authors.length > 0 ? book.authors[0] : ''
}

const getBookYear = (book: Book) => {
  const rawYear =
    book.year ??
    book.publishYear ??
    (book.releaseDate ? String(book.releaseDate).slice(0, 4) : undefined)
  return rawYear != null ? String(rawYear).replace(/[^0-9]/g, '') : ''
}

const getBookAsin = (book: Book) => {
  return (
    (book.asin && String(book.asin).trim()) || (book.asin13 && String(book.asin13).trim()) || ''
  )
}

const getQualityName = (id?: number | null) => {
  if (!id) return ''
  const found = (qualityProfiles.value || []).find(
    (q: { id?: number; name?: string }) => q.id === id,
  )
  return found ? (found.name ?? String(id)) : String(id)
}

const languageMap: Record<string, string> = {
  en: 'English',
  es: 'Spanish',
  fr: 'French',
  de: 'German',
  it: 'Italian',
  ja: 'Japanese',
}

const getLanguageName = (code?: string | null) => {
  if (!code) return ''
  return languageMap[code] || code
}

const isPathValid = computed(() => {
  return typeof selectedPath.value === 'string' && selectedPath.value.trim().length > 0
})

const openCellEditor = (item: PreviewItem, selectedField: string) => {
  cellEditorItems.value = [item]
  cellEditorField.value = selectedField
  importFields.forEach((currentField) => {
    if (currentField.key == selectedField) {
      cellEditorValue.value = currentField.get(item) ?? null
    }
  })
  showCellEditor.value = true
}

const openCellsEditor = () => {
  cellEditorItems.value = previewItems.value.filter(item => item.selected === true)
  cellEditorField.value = inputField.value
  cellEditorValue.value = null
  showCellEditor.value = true
  inputField.value = ''
}

const closeCellEditor = () => {
  showCellEditor.value = false
  cellEditorItems.value = []
  cellEditorField.value = null
  cellEditorValue.value = null
}

const saveCellEditor = () => {
  if (cellEditorItems.value.length <= 0 || !cellEditorField.value) return closeCellEditor()
  cellEditorItems.value.forEach((it) => {
    importFields.forEach((field) => {
      if (cellEditorField.value === field.key) {
        field.set(it, cellEditorValue.value ?? null)
      }
    })
    it.selected = true
  })
  closeCellEditor()
}

onMounted(async () => {
  await libraryStore.fetchLibrary()
  await configurationStore.loadQualityProfiles()
  loadRecentFolders()
})

watch(
  () => props.isOpen,
  async (v) => {
    // only auto-load preview when modal opens AND interactive preview mode is active
    if (v && selectedPath.value && showPreview.value) {
      await loadPreview()
    }
  },
)

watch(selectedPath, async (v) => {
  // only load preview automatically when interactive flow is active
  if (props.isOpen && v && showPreview.value) await loadPreview()
  // Save to recent folders when selecting a folder while modal is open
  if (props.isOpen && v) saveRecentFolder(v)
})

const loadPreview = async () => {
  if (!selectedPath.value) return
  loading.value = true
  try {
    const resp = await apiService.previewManualImport(selectedPath.value)
    // resp.items expected to be an array of detected files with metadata
    const items = Array.isArray(resp?.items) ? (resp.items as unknown[]) : []
    // only include common audio file extensions
    const audioExts = ['.mp3', '.m4b', '.m4a', '.flac', '.aac', '.ogg', '.wav', '.wma', '.opus']
    const filtered = items.filter((it) => {
      const obj = it as Record<string, unknown>
      const name =
        typeof obj.relativePath === 'string'
          ? obj.relativePath
          : typeof obj.fullPath === 'string'
            ? obj.fullPath
            : ''
      const lower = name.toLowerCase()
      return audioExts.some((ext) => lower.endsWith(ext))
    })
    previewItems.value = filtered.map((i) => {
      const obj = i as Record<string, unknown>
      const it: PreviewItem = {
        relativePath: typeof obj.relativePath === 'string' ? obj.relativePath : '',
        selected: false,
        matchedAudiobookId:
          typeof obj.matchedAudiobookId === 'number' ? obj.matchedAudiobookId : null,
        releaseGroup: typeof obj.releaseGroup === 'string' ? obj.releaseGroup : null,
        qualityProfileId: typeof obj.qualityProfileId === 'number' ? obj.qualityProfileId : null,
        language: typeof obj.language === 'string' ? obj.language : null,
        size: typeof obj.size === 'string' ? obj.size : null,
        fullPath: typeof obj.fullPath === 'string' ? obj.fullPath : null,
      }
      return it
    })
  } catch (err) {
    console.error('Failed to preview import:', err)
    previewItems.value = []
  } finally {
    loading.value = false
  }
}

// When user clicks a choice in the cell-editor choice list, set the value and immediately save
const selectEditorChoice = (choice: number | string | null) => {
  cellEditorValue.value = choice
  // Persist to the item then close the editor
  saveCellEditor()
}

const startAutomaticImport = async () => {
  if (!selectedPath.value) return
  loading.value = true
  try {
    // When running automatic import, send minimal request; backend will handle scanning
    const autoPayload: ManualImportRequest = { path: selectedPath.value, mode: 'automatic' }
    if (inputMode.value === 'move' || inputMode.value === 'hardlink/copy')
      autoPayload.inputMode = inputMode.value
    const resp = await apiService.startManualImport(autoPayload)
    // resp should contain import summary
    emit('imported', { imported: resp.importedCount ?? 0 })
    close()
  } catch (err) {
    console.error('Automatic import failed:', err)
  } finally {
    loading.value = false
  }
}

const startInteractiveImport = async () => {
  if (!selectedPath.value) return
  // Close inline browser if open, show preview area
  browserMode.value = false
  showPreview.value = true
  await loadPreview()
}

const importSelected = async () => {
  const selected = previewItems.value.filter((i) => i.selected)
  if (selected.length === 0) return
  loading.value = true
  try {
    // Map items to the payload the backend expects and ensure required fields are present
    const payloadItems = selected
      .filter((i) => i.fullPath && i.fullPath.length > 0)
      .map((i) => ({
        relativePath: i.relativePath,
        fullPath: i.fullPath as string,
        matchedAudiobookId: i.matchedAudiobookId ?? undefined,
        releaseGroup: i.releaseGroup ?? undefined,
        qualityProfileId: i.qualityProfileId ?? undefined,
        language: i.language ?? undefined,
        size: i.size ?? undefined,
      }))

    const manualPayload: ManualImportRequest = {
      path: selectedPath.value,
      mode: 'interactive',
      items: payloadItems,
      inputMode: inputMode.value || 'hardlink/copy',
    }
    const resp = await apiService.startManualImport(manualPayload)
    emit('imported', { imported: resp.importedCount ?? selected.length })
    close()
  } catch (err) {
    console.error('Manual import failed:', err)
  } finally {
    loading.value = false
  }
}

const close = () => {
  // reset preview state when closing
  showPreview.value = false
  previewItems.value = []
  emit('close')
}

 
const closeMatch = () => {
  showMatch.value = false
  matchTarget.value = null
}

const confirmMatch = async () => {
  if (!matchTarget.value || !matchSelection.value) return
  // Attach chosen audiobook id to the preview item so it will be imported to that audiobook
  matchTarget.value.matchedAudiobookId = matchSelection.value
  closeMatch()
}

const selectedCount = computed(() => previewItems.value.filter((i) => i.selected).length)

const allSelected = computed(
  () => previewItems.value.length > 0 && previewItems.value.every((i) => i.selected),
)

const setAllSelected = (value: boolean) => {
  previewItems.value.forEach((i) => (i.selected = Boolean(value)))
}

const getItemIssues = (item: PreviewItem): string[] => {
  const issues: string[] = []

  // Check for rejections from backend
  if (item.rejections && item.rejections.length > 0) {
    issues.push(...item.rejections)
  }

  // Check for missing required fields
  if (!item.matchedAudiobookId) {
    issues.push('No audiobook matched')
  }
  if (!item.qualityProfileId) {
    issues.push('No quality profile')
  }
  if (!item.language) {
    issues.push('No language specified')
  }

  return issues
}
</script>

<style scoped>
/* Remove custom modal styles - use shared Modal component styles from modals.css */

.top-path {
  width: 100%;
}

.center-actions {
  display: flex;
  gap: 1rem;
  justify-content: center;
  margin: 0.5rem 0 1rem 0;
}

.preview-area {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.preview-table {
  border: 1px solid #333;
  border-radius: 6px;
  overflow: hidden;
}

.preview-header {
  display: flex;
  padding: 0.5rem;
  background: #2f2f2f;
  color: #ccc;
  font-weight: 500;
}

.preview-row {
  display: flex;
  padding: 0.65rem 0.6rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
  align-items: center;
  height: 56px;
  background: #2b2b2b;
}

.preview-row:hover {
  background: #323232;
}

.col {
  padding: 0 0.6rem;
  display: flex;
  align-items: center;
  color: #e6e6e6;
}

.col-check {
  width: 44px;
  justify-content: center;
}

.col-check input[type='checkbox'] {
  width: 18px;
  height: 18px;
  accent-color: #ffffff;
  border-radius: 6px;
}

.col-path {
  flex: 1;
  min-width: 320px;
  font-size: 0.95rem;
}

.col-audiobook {
  width: 180px;
  color: #cfcfcf;
  font-size: 0.9rem;
  max-width: 180px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.col-release-group {
  width: 100px;
  font-size: 0.9rem;
}

.col-quality {
  width: 110px;
  font-size: 0.9rem;
}

.col-language {
  width: 120px;
  display: flex;
  gap: 0.4rem;
  align-items: center;
}

.col-size {
  width: 110px;
  justify-content: flex-end;
  font-weight: 500;
}

.col-action {
  width: 48px;
  justify-content: center;
}

.info-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  color: #3498db;
  font-size: 1.2rem;
  cursor: help;
  transition: color 0.2s;
}

.info-icon:hover {
  color: #2980b9;
}

/* Clickable empty cells used to open the cell editor */
.clickable-cell {
  min-height: 34px;
  min-width: 100%;
  display: flex;
  align-items: center;
  padding: 0.25rem 0;
  border-radius: 6px;
  cursor: pointer;
}

.clickable-cell .placeholder {
  display: inline-block;
  width: 100%;
  height: 100%;
  border: 1px dashed rgba(255, 255, 255, 0.12);
  border-radius: 6px;
  box-sizing: border-box;
}

.clickable-cell:hover .placeholder {
  border-color: rgba(255, 255, 255, 0.22);
}

.clickable-cell:focus-within .placeholder {
  border-color: var(--brand-500);
}

/* Form elements */
.form-select,
.form-input {
  width: 100%;
  padding: 0.5rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
  font-size: 0.85rem;
  transition: all 0.2s;
}

.form-select:focus,
.form-input:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.relative {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* modal-footer styles are centralized in src/assets/modals.css; keep this modal's layout preference */
.modal-footer {
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
}

.footer-left {
  display: flex;
  gap: 0.75rem;
  align-items: center;
  justify-content: flex-start;
  flex: 1 1 auto; /* occupy remaining space so select stays left */
}

.footer-right {
  display: flex;
  gap: 0.5rem;
  justify-content: flex-end;
  min-width: 0;
}

.mode-select,
.extra-select {
  background: #333;
  color: #ddd;
  border: 1px solid #444;
  padding: 0.5rem;
  border-radius: 6px;
}

/* Button color variants are centralized in `src/assets/modals.css` */
/* Use semantic classes like `cancel-button`, `btn-info`, `btn-primary`, `delete-button` */

/* Loading state */
.loading-state {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  padding: 2rem;
  color: #ccc;
}

/* Empty state */
.preview-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
  color: #999;
}

.preview-empty p {
  margin: 0;
}

.match-content {
  background: #2a2a2a;
  border: 1px solid #444;
  padding: 1.5rem;
  border-radius: 6px;
  width: 100%;
  max-width: 1000px; /* standardized to modal-lg (1000px) */
  box-shadow: 0 10px 40px rgba(0, 0, 0, 0.5);
  z-index: 1110;
}

.match-content h4 {
  margin: 0 0 1rem 0;
  color: #fff;
  font-size: 1.2rem;
}

.match-content select {
  width: 100%;
  padding: 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
  margin-bottom: 1rem;
}

.match-actions {
  display: flex;
  gap: 1rem;
  justify-content: flex-end;
}

.modal-body.browser-mode .center-actions,
.modal-body.browser-mode .preview-area {
  display: none;
}

.modal-body.browser-mode .top-path {
  position: relative;
  z-index: 10;
}

:deep(.browser-body) {
  max-height: unset !important;
  overflow: unset !important;
}

/* Audiobook table used in cell editor modal */
.audiobook-table {
  border: 1px solid #333;
  border-radius: 6px;
  background: #1f1f1f;
  overflow: hidden;
}

.table-header {
  display: flex;
  background: #292929;
  color: #dcdcdc;
  font-weight: 500;
  border-bottom: 1px solid #333;
}

.table-body {
  max-height: 320px;
  overflow-y: auto;
}

.table-row {
  display: flex;
  align-items: center;
  padding: 0.6rem 0.75rem;
  color: #e8e8e8;
  cursor: pointer;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
  transition: background-color 0.2s;
}

.table-row:hover {
  background: #232323;
}

.table-row.active {
  background: linear-gradient(90deg, rgba(33, 150, 243, 0.06), rgba(33, 150, 243, 0.02));
  border-left: 4px solid var(--brand-500);
}

.table-col {
  padding: 0 0.5rem;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.col-audiobook {
  flex: 2;
  min-width: 200px;
  font-weight: 500;
}

.col-author {
  flex: 1;
  min-width: 120px;
}

.col-year {
  flex: 0 0 80px;
  text-align: center;
}

.col-asin {
  flex: 1;
  min-width: 120px;
  font-family: monospace;
  font-size: 0.9rem;
}

.col-quality-profile {
  flex: 1;
  min-width: 150px;
  font-weight: 500;
}

.col-quality-description {
  flex: 2;
  min-width: 250px;
}

.col-language-name {
  flex: 1;
  min-width: 200px;
}

.editor-row {
  margin-bottom: 1rem;
}

.recent-folders {
  margin-bottom: 0.8rem;
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
  align-items: center;
}
.recent-title {
  color: #cfcfcf;
  font-weight: 500;
}
.recent-list {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  justify-content: center;
}
.recent-item {
  background: #1f1f1f;
  border: 1px solid #333;
  color: #e8e8e8;
  padding: 0.45rem 0.6rem;
  border-radius: 6px;
  cursor: pointer;
}
.recent-item:hover {
  border-color: #444;
}
</style>
