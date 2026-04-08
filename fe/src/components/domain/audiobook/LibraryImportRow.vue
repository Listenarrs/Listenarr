<template>
  <tr class="import-row" :class="{ selected: item.selected, 'no-match': item.hasSearched && !item.selectedMatch }">
    <td class="cell-check">
      <input
        type="checkbox"
        :checked="item.selected"
        :disabled="!item.selectedMatch"
        @change="store.toggleSelect(item.id)"
      />
    </td>

    <td class="cell-folder" data-label="Book">
      <span class="folder-name" :title="bookDisplayTitle">{{ bookDisplayTitle }}</span>
      <span class="folder-meta" v-if="bookMetaLine">{{ bookMetaLine }}</span>
      <span
        v-if="item.detectedTitle && item.detectedTitle.trim() && item.detectedTitle.trim() !== item.folderName"
        class="folder-origin"
        :title="item.folderName"
      >
        Folder: {{ item.folderName }}
      </span>
    </td>

    <td class="cell-file-path" data-label="Path">
      <span class="file-path" :title="item.fullPath">{{ item.fullPath }}</span>
      <details v-if="item.sourceFiles.length > 1" class="grouped-files">
        <summary class="grouped-files-summary">
          View grouped files ({{ item.sourceFiles.length }})
        </summary>
        <ul class="grouped-files-list">
          <li
            v-for="sourceFile in item.sourceFiles"
            :key="sourceFile"
            class="grouped-file-item"
          >
            <span class="grouped-file-label" :title="sourceFile">{{ formatGroupedFileLabel(sourceFile) }}</span>
            <span v-if="sourceFile === item.fullPath" class="grouped-file-badge">Row path</span>
          </li>
        </ul>
      </details>
    </td>

    <td class="cell-format" data-label="Format">
      <span class="format-badge">{{ item.format }}</span>
      <span class="file-count" v-if="item.fileCount > 1">{{ item.fileCount }} files</span>
    </td>

    <td class="cell-match" data-label="Match">
      <div class="match-area">
        <div v-if="item.isSearching" class="match-status searching">
          <PhSpinner class="ph-spin" :size="14" />
          <span>Searching...</span>
        </div>

        <div v-else-if="item.selectedMatch" class="match-status matched">
          <PhCheckCircle :size="14" class="match-icon-ok" />
          <div class="match-copy">
            <span
              class="match-title"
              :title="item.selectedMatch.asin ? `ASIN: ${item.selectedMatch.asin}` : undefined"
            >
              {{ item.selectedMatch.title }}
            </span>
            <span
              v-if="item.selectedMatch.authors?.length"
              class="match-author"
              :class="{ 'author-mismatch': isAuthorMismatch(item) }"
              :title="isAuthorMismatch(item) ? `Detected: ${item.detectedAuthor}` : undefined"
            >
              {{ item.selectedMatch.authors[0]?.name }}
            </span>
          </div>
          <button class="btn-clear-match" title="Clear match" @click="store.clearMatch(item.id)">x</button>
        </div>

        <div v-else-if="item.hasSearched" class="match-status no-match">
          <PhWarningCircle :size="14" class="match-icon-warn" />
          <span>No match found</span>
        </div>

        <div v-else class="match-status unsearched">
          <span>-</span>
        </div>

        <button
          class="btn-search-toggle"
          title="Search for a match"
          @click="emit('search', item)"
        >
          <PhMagnifyingGlass :size="14" />
        </button>
      </div>
    </td>
  </tr>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  PhSpinner,
  PhCheckCircle,
  PhWarningCircle,
  PhMagnifyingGlass,
} from '@phosphor-icons/vue'
import { useLibraryImportStore } from '@/stores/libraryImport'
import type { LibraryImportItem } from '@/stores/libraryImport'

const props = defineProps<{ item: LibraryImportItem }>()
const emit = defineEmits<{
  search: [item: LibraryImportItem]
}>()

const store = useLibraryImportStore()

const bookDisplayTitle = computed(() => props.item.detectedTitle?.trim() || props.item.folderName)
const bookMetaLine = computed(() =>
  [
    props.item.detectedAuthor,
    props.item.detectedSeries,
  ].filter(Boolean).join(' - '),
)

function isAuthorMismatch(item: LibraryImportItem): boolean {
  if (!item.detectedAuthor || !item.selectedMatch?.authors?.length) return false
  const detected = item.detectedAuthor.toLowerCase()
  const matched = (item.selectedMatch.authors[0]?.name ?? '').toLowerCase()
  return !!matched && !matched.includes(detected) && !detected.includes(matched)
}

function formatGroupedFileLabel(sourceFile: string): string {
  const normalizedSource = sourceFile.replace(/\\/g, '/')
  const normalizedFolder = props.item.folderPath.replace(/\\/g, '/').replace(/\/+$/, '')

  if (normalizedFolder && normalizedSource.startsWith(`${normalizedFolder}/`)) {
    return normalizedSource.slice(normalizedFolder.length + 1)
  }

  return normalizedSource.split('/').pop() ?? sourceFile
}
</script>

<style scoped>
.import-row td {
  padding: 0.55rem 0.75rem;
  vertical-align: top;
  border-bottom: 1px solid #2a2a2a;
}

.import-row.selected td {
  background-color: rgba(var(--brand-500-rgb, 99, 102, 241), 0.06);
}

.cell-check {
  width: 2.5rem;
  text-align: center;
}

.cell-check input[type='checkbox'] {
  width: 1rem;
  height: 1rem;
  cursor: pointer;
}

.cell-check input:disabled {
  opacity: 0.3;
  cursor: not-allowed;
}

.cell-folder {
  max-width: 280px;
  min-width: 0;
}

.folder-name {
  display: block;
  font-family: monospace;
  font-size: 0.85rem;
  color: #e0e0e0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.folder-meta {
  display: block;
  font-size: 0.75rem;
  color: #888;
  margin-top: 0.25rem;
  white-space: normal;
  overflow-wrap: anywhere;
}

.folder-origin {
  display: block;
  margin-top: 0.18rem;
  font-size: 0.68rem;
  color: #5f6877;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.cell-file-path {
  min-width: 320px;
}

.file-path {
  display: block;
  margin-top: 0.2rem;
  font-family: monospace;
  font-size: 0.72rem;
  color: #7f8a9a;
  line-height: 1.35;
  white-space: normal;
  overflow-wrap: anywhere;
  word-break: break-word;
}

.grouped-files {
  margin-top: 0.45rem;
}

.grouped-files-summary {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  cursor: pointer;
  color: #a7b0c0;
  font-size: 0.72rem;
  font-weight: 600;
  list-style: none;
}

.grouped-files-summary::-webkit-details-marker {
  display: none;
}

.grouped-files-summary::before {
  content: '>';
  display: inline-block;
  font-size: 0.7rem;
  line-height: 1;
  transform: rotate(0deg);
  transition: transform 0.16s ease;
  color: #7f8a9a;
}

.grouped-files[open] .grouped-files-summary::before {
  transform: rotate(90deg);
}

.grouped-files-list {
  margin: 0.45rem 0 0;
  padding: 0.55rem 0.65rem;
  list-style: none;
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.025);
  display: grid;
  gap: 0.35rem;
}

.grouped-file-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.6rem;
  min-width: 0;
}

.grouped-file-label {
  min-width: 0;
  font-family: monospace;
  font-size: 0.7rem;
  color: #8f99aa;
  line-height: 1.35;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.grouped-file-badge {
  flex-shrink: 0;
  font-size: 0.62rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: #d2d8e7;
  background: rgba(99, 102, 241, 0.16);
  border: 1px solid rgba(129, 140, 248, 0.24);
  border-radius: 999px;
  padding: 0.14rem 0.42rem;
}

.cell-format {
  width: 6rem;
  white-space: nowrap;
}

.format-badge {
  display: inline-block;
  font-size: 0.7rem;
  background: #333;
  color: #aaa;
  border-radius: 999px;
  padding: 0.16rem 0.5rem;
  text-transform: uppercase;
}

.file-count {
  display: block;
  font-size: 0.7rem;
  color: #888;
  margin-top: 0.18rem;
}

.cell-match {
  min-width: 280px;
}

.match-area {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: nowrap;
}

.match-status {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 0.82rem;
  flex: 1;
  min-width: 0;
}

.match-copy {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  min-width: 0;
}

.match-status.searching {
  color: #888;
}

.match-status.matched {
  color: #e0e0e0;
}

.match-icon-ok {
  color: #4caf50;
  flex-shrink: 0;
}

.match-icon-warn {
  color: #f59e0b;
  flex-shrink: 0;
}

.match-title {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 200px;
}

.match-author {
  font-size: 0.75rem;
  color: #888;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 120px;
}

.match-author.author-mismatch {
  color: #f59e0b;
}

.match-status.no-match {
  color: #f59e0b;
}

.match-status.unsearched {
  color: #555;
}

.btn-clear-match {
  background: none;
  border: none;
  color: #888;
  cursor: pointer;
  font-size: 0.95rem;
  line-height: 1;
  padding: 0 0.2rem;
  flex-shrink: 0;
  text-transform: uppercase;
}

.btn-clear-match:hover {
  color: #ef4444;
}

.btn-search-toggle {
  background: none;
  border: 1px solid #444;
  border-radius: 8px;
  color: #888;
  cursor: pointer;
  padding: 0.28rem 0.45rem;
  flex-shrink: 0;
  display: flex;
  align-items: center;
}

.btn-search-toggle:hover {
  border-color: var(--brand-500, #6366f1);
  color: var(--brand-500, #6366f1);
}

@media (max-width: 720px) {
  .import-row {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    grid-template-areas:
      'check folder format'
      'path path path'
      'match match match';
    gap: 0.7rem 0.8rem;
    padding: 0.85rem;
    border: 1px solid #2d2d2d;
    border-radius: 16px;
    background: linear-gradient(180deg, #171717 0%, #121212 100%);
    box-shadow: 0 14px 32px rgba(0, 0, 0, 0.18);
  }

  .import-row td {
    display: block;
    padding: 0;
    border-bottom: none;
    background: transparent;
  }

  .import-row.selected td {
    background: transparent;
  }

  .cell-check {
    grid-area: check;
    width: auto;
    padding-top: 0.2rem;
  }

  .cell-folder {
    grid-area: folder;
    max-width: none;
  }

  .cell-file-path {
    grid-area: path;
    min-width: 0;
    padding: 0.65rem 0.75rem 0.7rem;
    border: 1px solid rgba(255, 255, 255, 0.05);
    border-radius: 12px;
    background: rgba(255, 255, 255, 0.02);
  }

  .cell-format {
    grid-area: format;
    width: auto;
    justify-self: end;
    text-align: right;
  }

  .cell-match {
    grid-area: match;
    min-width: 0;
    padding: 0.65rem 0.75rem 0.7rem;
    border: 1px solid rgba(255, 255, 255, 0.05);
    border-radius: 12px;
    background: rgba(255, 255, 255, 0.02);
  }

  .cell-file-path::before,
  .cell-match::before {
    content: attr(data-label);
    display: block;
    margin-bottom: 0.45rem;
    font-size: 0.64rem;
    text-transform: uppercase;
    letter-spacing: 0.12em;
    color: #6e7788;
  }

  .folder-name {
    white-space: normal;
    overflow: visible;
    text-overflow: unset;
    font-size: 0.95rem;
    line-height: 1.3;
    font-weight: 600;
    letter-spacing: -0.01em;
  }

  .folder-meta {
    margin-top: 0.22rem;
    font-size: 0.78rem;
    line-height: 1.45;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .format-badge {
    font-size: 0.66rem;
    padding: 0.22rem 0.55rem;
    background: rgba(255, 255, 255, 0.08);
    color: #d5d7dd;
  }

  .file-count {
    margin-top: 0.26rem;
  }

  .match-area {
    align-items: center;
    gap: 0.65rem;
    flex-wrap: nowrap;
  }

  .match-status {
    width: auto;
    align-items: flex-start;
    gap: 0.45rem;
  }

  .match-copy {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.16rem;
  }

  .match-title,
  .match-author {
    max-width: none;
    white-space: normal;
  }

  .match-title {
    font-size: 0.84rem;
    line-height: 1.3;
  }

  .match-author {
    font-size: 0.72rem;
    line-height: 1.2;
  }

  .file-path {
    margin-top: 0;
    font-size: 0.73rem;
    line-height: 1.45;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .grouped-files {
    margin-top: 0.5rem;
  }

  .grouped-files-list {
    padding: 0.55rem 0.6rem;
  }

  .grouped-file-item {
    align-items: flex-start;
    flex-direction: column;
    gap: 0.22rem;
  }

  .grouped-file-label {
    white-space: normal;
    overflow: visible;
    text-overflow: unset;
    overflow-wrap: anywhere;
    word-break: break-word;
  }

  .btn-clear-match {
    align-self: center;
  }

  .btn-search-toggle {
    margin-left: auto;
    align-self: center;
    padding: 0.35rem 0.5rem;
    border-color: rgba(255, 255, 255, 0.14);
    background: rgba(255, 255, 255, 0.03);
  }
}

@media (max-width: 520px) {
  .import-row {
    grid-template-columns: auto minmax(0, 1fr);
    grid-template-areas:
      'check folder'
      'format format'
      'path path'
      'match match';
  }

  .cell-format {
    justify-self: start;
    text-align: left;
  }

  .cell-file-path,
  .cell-match {
    padding: 0.6rem 0.7rem 0.65rem;
  }

  .file-count {
    display: inline;
    margin-top: 0;
    margin-left: 0.45rem;
  }

  .match-area {
    align-items: flex-start;
    flex-wrap: wrap;
  }

  .match-status {
    width: 100%;
  }

  .btn-search-toggle {
    margin-left: 0;
  }
}
</style>
