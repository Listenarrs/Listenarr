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
  <div class="library-import-view">
    <div class="page-header">
      <h1>Library Import</h1>
      <p class="page-subtitle">
        Scan a root folder for audio files not in your library, match them to audiobooks, and import
        them.
      </p>
    </div>

    <div class="scan-controls">
      <div class="folder-select-wrap">
        <label class="control-label">Root folder</label>
        <select v-model="selectedFolderId" class="folder-select" @change="onFolderChange">
          <option v-for="f in rootFoldersStore.folders" :key="f.id" :value="f.id">
            {{ f.name || f.path }}
          </option>
        </select>
      </div>

      <button
        class="btn btn-secondary btn-sm"
        :disabled="store.scanStatus === 'scanning' || !filesystemReadinessStore.filesystemReady"
        :title="
          !filesystemReadinessStore.filesystemReady
            ? 'Available after library filesystem initialization completes'
            : undefined
        "
        @click="openAddRootFolder"
      >
        <PhFolderPlus :size="15" />
        Choose another folder...
      </button>

      <button
        class="btn btn-primary btn-sm"
        :disabled="
          !selectedFolderId ||
          store.scanStatus === 'scanning' ||
          !filesystemReadinessStore.filesystemReady
        "
        :title="
          !filesystemReadinessStore.filesystemReady
            ? 'Available after library filesystem initialization completes'
            : undefined
        "
        @click="startScan"
      >
        <PhSpinner v-if="store.scanStatus === 'scanning'" class="ph-spin" :size="15" />
        <PhMagnifyingGlass v-else :size="15" />
        {{ store.scanStatus === 'scanning' ? 'Scanning...' : 'Scan' }}
      </button>

      <span v-if="store.lastScannedAt" class="scan-meta">
        Last scanned {{ timeAgo(store.lastScannedAt) }}
        <span v-if="store.itemList.length > 0"> · {{ store.itemList.length }} unmatched</span>
      </span>

      <span v-if="store.scanStatus === 'error'" class="scan-error">
        <PhWarning :size="14" /> {{ store.scanError ?? 'Scan failed' }}
      </span>
    </div>

    <div v-if="store.scanStatus === 'scanning'" class="state-panel">
      <PhSpinner class="ph-spin state-icon" />
      <p>Scanning for unmatched audio files...</p>
    </div>

    <div v-else-if="store.scanStatus === 'idle' && store.itemList.length === 0" class="state-panel">
      <PhMagnifyingGlass class="state-icon" />
      <h3>No scan results yet</h3>
      <p>Select a root folder and click <strong>Scan</strong> to find unmatched audio files.</p>
    </div>

    <div v-else-if="store.scanStatus === 'done' && store.itemList.length === 0" class="state-panel">
      <PhCheckCircle class="state-icon ok" />
      <h3>All files are in your library</h3>
      <p>No unmatched audio files were found.</p>
    </div>

    <div v-else-if="store.itemList.length > 0" class="results-section">
      <div class="table-toolbar">
        <div class="table-summary">
          <span class="summary-pill">{{ sortedItems.length }} items</span>
          <span class="summary-text">
            Sorted by {{ currentSortLabel }} ({{ sortDirectionLabel }})
          </span>
        </div>

        <div class="mobile-sort-controls">
          <label class="mobile-sort-control">
            <span>Sort</span>
            <select v-model="sortKey" class="sort-select">
              <option v-for="option in sortOptions" :key="option.value" :value="option.value">
                {{ option.label }}
              </option>
            </select>
          </label>

          <label class="mobile-sort-control">
            <span>Order</span>
            <select v-model="sortDirection" class="sort-select">
              <option value="asc">Ascending</option>
              <option value="desc">Descending</option>
            </select>
          </label>
        </div>
      </div>

      <div class="table-wrap">
        <table class="import-table" :style="{ minWidth: `${tableMinWidth}px` }">
          <colgroup>
            <col class="col-check" />
            <col :style="{ width: `${columnWidths.folder}px` }" />
            <col :style="{ width: `${columnWidths.path}px` }" />
            <col :style="{ width: `${columnWidths.format}px` }" />
            <col :style="{ width: `${columnWidths.match}px` }" />
          </colgroup>

          <thead>
            <tr>
              <th class="col-check">
                <input
                  type="checkbox"
                  :checked="allMatchedSelected"
                  :indeterminate="someSelected && !allMatchedSelected"
                  @change="store.toggleSelectAll()"
                  title="Select all matched"
                />
              </th>

              <th class="sortable-header" :aria-sort="getAriaSort('folder')">
                <button type="button" class="sort-button" @click="setSort('folder')">
                  <span class="header-content">
                    Book
                    <component :is="getSortIcon('folder')" class="sort-icon" />
                  </span>
                </button>
                <span
                  class="resize-handle"
                  :class="{ active: resizingColumn === 'folder' }"
                  @pointerdown="startResize('folder', $event)"
                />
              </th>

              <th class="sortable-header col-path" :aria-sort="getAriaSort('path')">
                <button type="button" class="sort-button" @click="setSort('path')">
                  <span class="header-content">
                    Path
                    <component :is="getSortIcon('path')" class="sort-icon" />
                  </span>
                </button>
                <span
                  class="resize-handle"
                  :class="{ active: resizingColumn === 'path' }"
                  @pointerdown="startResize('path', $event)"
                />
              </th>

              <th class="sortable-header col-format" :aria-sort="getAriaSort('format')">
                <button type="button" class="sort-button" @click="setSort('format')">
                  <span class="header-content">
                    Format
                    <component :is="getSortIcon('format')" class="sort-icon" />
                  </span>
                </button>
                <span
                  class="resize-handle"
                  :class="{ active: resizingColumn === 'format' }"
                  @pointerdown="startResize('format', $event)"
                />
              </th>

              <th class="sortable-header col-match" :aria-sort="getAriaSort('match')">
                <button type="button" class="sort-button" @click="setSort('match')">
                  <span class="header-content">
                    Match
                    <component :is="getSortIcon('match')" class="sort-icon" />
                  </span>
                </button>
                <span
                  class="resize-handle"
                  :class="{ active: resizingColumn === 'match' }"
                  @pointerdown="startResize('match', $event)"
                />
              </th>
            </tr>
          </thead>

          <tbody>
            <LibraryImportRow v-for="item in sortedItems" :key="item.id" :item="item" />
          </tbody>
        </table>
      </div>
    </div>

    <LibraryImportFooter
      v-if="rootFoldersStore.folders.length > 0"
      :folders="rootFoldersStore.folders"
      :source-folder="selectedSourceFolder"
    />
  </div>

  <RootFolderFormModal
    v-if="addRootFolder"
    @saved="refreshRootFolders"
    @close="closeAddRootFolder"
  />
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import {
  PhSpinner,
  PhMagnifyingGlass,
  PhWarning,
  PhCheckCircle,
  PhArrowDown,
  PhArrowUp,
  PhArrowsDownUp,
  PhFolderPlus,
} from '@phosphor-icons/vue'
import { useLibraryImportStore } from '@/stores/libraryImport'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useConfigurationStore } from '@/stores/configuration'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import LibraryImportRow from '@/components/domain/audiobook/LibraryImportRow.vue'
import LibraryImportFooter from '@/components/domain/audiobook/LibraryImportFooter.vue'
import {
  DEFAULT_LIBRARY_IMPORT_COLUMN_WIDTHS,
  LIBRARY_IMPORT_COLUMN_MIN_WIDTHS,
  sortLibraryImportItems,
  type LibraryImportColumnWidths,
  type LibraryImportResizableColumnKey,
  type LibraryImportSortDirection,
  type LibraryImportSortKey,
} from '@/utils/libraryImportTable'
import RootFolderFormModal from '@/components/settings/RootFolderFormModal.vue'
import type { RootFolder } from '@/types'

const COLUMN_WIDTH_STORAGE_KEY = 'listenarr.libraryImport.columnWidths.v1'
const MAX_COLUMN_WIDTH = 960
const MOBILE_TABLE_BREAKPOINT = 720

const store = useLibraryImportStore()
const rootFoldersStore = useRootFoldersStore()
const configStore = useConfigurationStore()
const filesystemReadinessStore = useFilesystemReadinessStore()

const selectedFolderId = ref<number | null>(null)
const selectedSourceFolder = computed(() =>
  rootFoldersStore.folders.find((folder) => folder.id === selectedFolderId.value),
)
const sortKey = ref<LibraryImportSortKey>('folder')
const sortDirection = ref<LibraryImportSortDirection>('asc')
const columnWidths = ref<LibraryImportColumnWidths>({ ...DEFAULT_LIBRARY_IMPORT_COLUMN_WIDTHS })
const resizingColumn = ref<LibraryImportResizableColumnKey | null>(null)
const addRootFolder = ref<boolean>(false)

const sortOptions: Array<{ value: LibraryImportSortKey; label: string }> = [
  { value: 'folder', label: 'Book' },
  { value: 'path', label: 'Path' },
  { value: 'format', label: 'Format' },
  { value: 'match', label: 'Match' },
]

const allMatchedSelected = computed(() => {
  const matched = store.itemList.filter((item) => item.selectedMatch)
  return matched.length > 0 && matched.every((item) => item.selected)
})

const someSelected = computed(() => store.selectedCount > 0)

const sortedItems = computed(() =>
  sortLibraryImportItems(store.itemList, sortKey.value, sortDirection.value),
)

const currentSortLabel = computed(
  () => sortOptions.find((option) => option.value === sortKey.value)?.label ?? 'Book',
)

const sortDirectionLabel = computed(() =>
  sortDirection.value === 'asc' ? 'ascending' : 'descending',
)

const tableMinWidth = computed(
  () =>
    44 +
    columnWidths.value.folder +
    columnWidths.value.path +
    columnWidths.value.format +
    columnWidths.value.match,
)

let resizeState: {
  key: LibraryImportResizableColumnKey
  startX: number
  startWidth: number
} | null = null

onMounted(async () => {
  if (rootFoldersStore.folders.length === 0) await rootFoldersStore.load()
  await configStore.loadApplicationSettings()
  loadColumnWidths()

  const defaultFolder = rootFoldersStore.defaultFolder ?? rootFoldersStore.folders[0] ?? null
  if (defaultFolder) {
    selectedFolderId.value = defaultFolder.id
    await store.initFromRootFolder(defaultFolder.id)
  }

  store.action = 'none'
})

onBeforeUnmount(() => {
  stopResize()
})

async function onFolderChange() {
  if (selectedFolderId.value) {
    store.stopProcessing()
    await store.initFromRootFolder(selectedFolderId.value)
  }
}

async function startScan() {
  if (!selectedFolderId.value) return
  await store.triggerScan(selectedFolderId.value)
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

function setSort(column: LibraryImportSortKey) {
  if (sortKey.value === column) {
    sortDirection.value = sortDirection.value === 'asc' ? 'desc' : 'asc'
    return
  }

  sortKey.value = column
  sortDirection.value = 'asc'
}

function getSortIcon(column: LibraryImportSortKey) {
  if (sortKey.value !== column) return PhArrowsDownUp
  return sortDirection.value === 'asc' ? PhArrowUp : PhArrowDown
}

function getAriaSort(column: LibraryImportSortKey): 'ascending' | 'descending' | 'none' {
  if (sortKey.value !== column) return 'none'
  return sortDirection.value === 'asc' ? 'ascending' : 'descending'
}

function startResize(column: LibraryImportResizableColumnKey, event: PointerEvent) {
  if (
    typeof window !== 'undefined' &&
    window.matchMedia(`(max-width: ${MOBILE_TABLE_BREAKPOINT}px)`).matches
  ) {
    return
  }

  event.preventDefault()
  event.stopPropagation()

  resizeState = {
    key: column,
    startX: event.clientX,
    startWidth: columnWidths.value[column],
  }
  resizingColumn.value = column

  window.addEventListener('pointermove', handleResize)
  window.addEventListener('pointerup', stopResize)
  document.body.style.cursor = 'col-resize'
  document.body.style.userSelect = 'none'
}

function handleResize(event: PointerEvent) {
  if (!resizeState) return

  const minWidth = LIBRARY_IMPORT_COLUMN_MIN_WIDTHS[resizeState.key]
  const nextWidth = Math.max(
    minWidth,
    Math.min(
      MAX_COLUMN_WIDTH,
      Math.round(resizeState.startWidth + event.clientX - resizeState.startX),
    ),
  )

  columnWidths.value = {
    ...columnWidths.value,
    [resizeState.key]: nextWidth,
  }
}

function stopResize() {
  if (typeof window !== 'undefined') {
    window.removeEventListener('pointermove', handleResize)
    window.removeEventListener('pointerup', stopResize)
  }

  if (resizeState) persistColumnWidths()

  resizeState = null
  resizingColumn.value = null
  document.body.style.removeProperty('cursor')
  document.body.style.removeProperty('user-select')
}

function loadColumnWidths() {
  try {
    const raw = localStorage.getItem(COLUMN_WIDTH_STORAGE_KEY)
    if (!raw) return

    const parsed = JSON.parse(raw) as Partial<Record<LibraryImportResizableColumnKey, unknown>>
    const nextWidths: LibraryImportColumnWidths = { ...DEFAULT_LIBRARY_IMPORT_COLUMN_WIDTHS }

    for (const key of Object.keys(nextWidths) as LibraryImportResizableColumnKey[]) {
      const candidate = Number(parsed[key])
      if (!Number.isFinite(candidate)) continue

      nextWidths[key] = Math.max(
        LIBRARY_IMPORT_COLUMN_MIN_WIDTHS[key],
        Math.min(MAX_COLUMN_WIDTH, Math.round(candidate)),
      )
    }

    columnWidths.value = nextWidths
  } catch {
    columnWidths.value = { ...DEFAULT_LIBRARY_IMPORT_COLUMN_WIDTHS }
  }
}

function persistColumnWidths() {
  try {
    localStorage.setItem(COLUMN_WIDTH_STORAGE_KEY, JSON.stringify(columnWidths.value))
  } catch {
    // Non-fatal: resizing still works for the current session.
  }
}

function openAddRootFolder() {
  addRootFolder.value = true
}

function closeAddRootFolder() {
  addRootFolder.value = false
}

async function refreshRootFolders(newFolder: RootFolder) {
  closeAddRootFolder()

  await rootFoldersStore.load()

  if (newFolder) {
    selectedFolderId.value = newFolder.id
    await store.initFromRootFolder(newFolder.id)
  }
}
</script>

<style scoped>
.library-import-view {
  padding: 1.5rem;
  padding-bottom: 5rem;
}

.page-header {
  margin-bottom: 1.5rem;
}

.page-header h1 {
  margin: 0 0 0.25rem;
  color: white;
  font-size: 1.75rem;
}

.page-subtitle {
  color: #888;
  font-size: 0.875rem;
  margin: 0;
}

.scan-controls {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-bottom: 1.25rem;
  flex-wrap: wrap;
}

.control-label {
  font-size: 0.8rem;
  color: #888;
  margin-right: 0.25rem;
}

.folder-select-wrap {
  display: flex;
  align-items: center;
  gap: 0.4rem;
}

.folder-select {
  appearance: none;
  -webkit-appearance: none;
  background: #2a2a2a;
  background-image:
    linear-gradient(45deg, transparent 50%, #9aa3b2 50%),
    linear-gradient(135deg, #9aa3b2 50%, transparent 50%);
  background-position:
    calc(100% - 1rem) calc(50% - 0.1rem),
    calc(100% - 0.7rem) calc(50% - 0.1rem);
  background-size:
    0.35rem 0.35rem,
    0.35rem 0.35rem;
  background-repeat: no-repeat;
  border: 1px solid #444;
  border-radius: 4px;
  color: #e0e0e0;
  font-size: 0.85rem;
  padding: 0.4rem 2rem 0.4rem 0.6rem;
  height: var(--control-height, 40px);
  box-sizing: border-box;
  cursor: pointer;
  min-width: 180px;
}

.scan-meta {
  font-size: 0.8rem;
  color: #666;
}

.scan-error {
  display: flex;
  align-items: center;
  gap: 0.3rem;
  font-size: 0.8rem;
  color: #ef4444;
}

.state-panel {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 4rem 2rem;
  text-align: center;
  color: #888;
}

.state-icon {
  font-size: 3rem;
  width: 3rem;
  height: 3rem;
  color: #555;
  margin-bottom: 1rem;
}

.state-icon.ok {
  color: #22c55e;
}

.state-panel h3 {
  color: #ccc;
  margin: 0 0 0.5rem;
}

.state-panel p {
  margin: 0;
  font-size: 0.875rem;
}

.results-section {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.table-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.9rem 1rem;
  border: 1px solid #252525;
  border-radius: 12px;
  background:
    radial-gradient(circle at top left, rgba(99, 102, 241, 0.12), transparent 42%),
    linear-gradient(180deg, rgba(28, 28, 28, 0.98) 0%, rgba(18, 18, 18, 0.98) 100%);
  box-shadow: 0 12px 28px rgba(0, 0, 0, 0.18);
}

.table-summary {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.75rem;
  color: #8f96a3;
  font-size: 0.82rem;
  min-width: 0;
}

.summary-pill {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 4.5rem;
  padding: 0.35rem 0.65rem;
  border-radius: 999px;
  background: linear-gradient(135deg, rgba(99, 102, 241, 0.26), rgba(79, 70, 229, 0.12));
  border: 1px solid rgba(129, 140, 248, 0.34);
  color: #d7ddff;
  font-weight: 600;
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.05);
}

.summary-text {
  color: #7d8696;
  min-width: 0;
}

.mobile-sort-controls {
  display: none;
  align-items: end;
  gap: 0.75rem;
}

.mobile-sort-control {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  min-width: 0;
}

.mobile-sort-control span {
  font-size: 0.68rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: #7d8696;
}

.sort-select {
  appearance: none;
  -webkit-appearance: none;
  width: 100%;
  min-height: 2.5rem;
  background:
    linear-gradient(45deg, transparent 50%, #959db0 50%),
    linear-gradient(135deg, #959db0 50%, transparent 50%),
    linear-gradient(180deg, #212121 0%, #181818 100%);
  background-position:
    calc(100% - 1rem) calc(50% - 0.1rem),
    calc(100% - 0.7rem) calc(50% - 0.1rem),
    center;
  background-size:
    0.35rem 0.35rem,
    0.35rem 0.35rem,
    100% 100%;
  background-repeat: no-repeat;
  border: 1px solid #3b3b3b;
  border-radius: 8px;
  color: #ececec;
  padding: 0.55rem 2rem 0.55rem 0.7rem;
  font-size: 0.82rem;
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.03);
}

.sort-select:focus-visible,
.folder-select:focus-visible {
  outline: 2px solid rgba(99, 102, 241, 0.45);
  outline-offset: 1px;
}

.table-wrap {
  overflow: auto;
  border: 1px solid #2a2a2a;
  border-radius: 10px;
  background: linear-gradient(180deg, #171717 0%, #121212 100%);
  box-shadow: 0 16px 38px rgba(0, 0, 0, 0.2);
}

.import-table {
  width: 100%;
  border-collapse: collapse;
  table-layout: fixed;
  font-size: 0.875rem;
}

.import-table thead tr {
  background: #1e1e1e;
}

.import-table th {
  position: relative;
  padding: 0;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #888;
  border-bottom: 1px solid #2a2a2a;
  white-space: nowrap;
}

.col-check {
  width: 2.75rem;
  text-align: center;
}

.sort-button {
  appearance: none;
  -webkit-appearance: none;
  display: block;
  width: 100%;
  margin: 0;
  padding: 0.7rem 0.75rem;
  border: 0 !important;
  border-radius: 0;
  background: transparent !important;
  box-shadow: none !important;
  color: inherit;
  font: inherit;
  text-transform: inherit;
  letter-spacing: inherit;
  text-align: left;
  cursor: pointer;
}

.sortable-header:hover {
  background: #242424;
}

.sortable-header[aria-sort='ascending'],
.sortable-header[aria-sort='descending'] {
  color: #d0d6e4;
  background: linear-gradient(180deg, rgba(99, 102, 241, 0.08), rgba(99, 102, 241, 0.02));
}

.sort-button:focus-visible {
  outline: 2px solid var(--brand-500, #6366f1);
  outline-offset: -2px;
}

.header-content {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.6rem;
}

.sort-icon {
  font-size: 0.85rem;
  opacity: 0.72;
  flex-shrink: 0;
}

.resize-handle {
  position: absolute;
  top: 0;
  right: 0;
  width: 12px;
  height: 100%;
  cursor: col-resize;
  z-index: 2;
}

.resize-handle::after {
  content: '';
  position: absolute;
  top: 10px;
  bottom: 10px;
  left: 5px;
  width: 2px;
  border-radius: 999px;
  background: transparent;
  transition: background-color 0.15s ease;
}

.sortable-header:hover .resize-handle::after,
.resize-handle.active::after {
  background: rgba(255, 255, 255, 0.18);
}

.import-table th input[type='checkbox'] {
  width: 1rem;
  height: 1rem;
  cursor: pointer;
}

@media (max-width: 960px) {
  .table-toolbar {
    flex-wrap: wrap;
  }

  .table-summary {
    width: 100%;
    justify-content: space-between;
  }
}

@media (max-width: 720px) {
  .library-import-view {
    padding: 1rem;
    padding-bottom: 6rem;
  }

  .scan-controls {
    flex-direction: column;
    align-items: stretch;
  }

  .folder-select-wrap {
    width: 100%;
  }

  .folder-select {
    flex: 1;
    min-width: 0;
  }

  .scan-controls .btn {
    width: 100%;
    justify-content: center;
  }

  .table-toolbar {
    flex-direction: column;
    align-items: stretch;
    gap: 0.75rem;
  }

  .table-summary {
    justify-content: space-between;
  }

  .mobile-sort-controls {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 0.75rem;
  }

  .table-wrap {
    overflow: visible;
    border: none;
    background: none;
  }

  .import-table,
  .import-table tbody,
  .import-table tr {
    display: block;
    width: 100%;
  }

  .import-table {
    min-width: 0 !important;
  }

  .import-table colgroup,
  .import-table thead {
    display: none;
  }

  .import-table tbody {
    display: grid;
    gap: 0.85rem;
  }
}

@media (max-width: 520px) {
  .mobile-sort-controls {
    grid-template-columns: 1fr;
  }

  .summary-pill {
    min-width: auto;
  }

  .table-summary {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.45rem;
  }
}
</style>
