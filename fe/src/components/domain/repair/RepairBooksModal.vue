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
      <ModalHeader title="Repair Books" :icon="PhWrench" @close="handleClose" />
    </template>

    <template #default>
      <ModalBody compact maxHeight="72vh" class="repair-modal-body">
        <div v-if="loading" class="repair-state">
          <PhSpinner class="ph-spin repair-icon" />
          <p>Loading the selected audiobooks…</p>
        </div>

        <div v-else-if="error" class="repair-state repair-error">
          <PhWarningCircle class="repair-icon" />
          <p>{{ error }}</p>
        </div>

        <div v-else-if="executing" class="repair-state">
          <PhSpinner class="ph-spin repair-icon" />
          <p>Repairing {{ progress }} of {{ selectedCount }}…</p>
        </div>

        <div v-else-if="!finished && rows.length" class="repair-list-wrapper">
          <div class="info-section repair-info-section">
            <PhInfo />
            <p>
              Point each book at the right Audible record: search its title and author in the region
              and pick the result under the book, or type the ASIN and its region (it is checked in
              that region). Changing the title, author or region empties the ASIN, which no longer
              matches: search and pick the book again. Repairing saves the ASIN and its region and
              rescans the metadata from it.
            </p>
          </div>

          <div class="form-group repair-group">
            <label class="form-label">
              <PhCheckSquare />
              Selection
            </label>
            <div class="form-control-card">
              <div class="repair-toolbar">
                <p class="repair-toolbar-title">
                  <strong>{{ selectedCount }}</strong> of {{ rows.length }} audiobook(s) selected
                </p>
                <div class="toolbar-actions">
                  <button
                    type="button"
                    class="btn btn-secondary repair-action-btn"
                    @click="selectAll"
                  >
                    Select All
                  </button>
                  <button
                    type="button"
                    class="btn btn-secondary repair-action-btn"
                    @click="clearSelection"
                  >
                    Clear
                  </button>
                </div>
              </div>
            </div>
          </div>

          <div class="form-group repair-group">
            <label class="form-label">
              <PhWrench />
              Books
            </label>
            <div class="repair-list">
              <div
                v-for="row in rows"
                :key="row.id"
                class="form-control-card repair-card"
                :data-testid="`repair-row-${row.id}`"
              >
                <label class="repair-header">
                  <span class="repair-heading">
                    <input
                      type="checkbox"
                      class="repair-checkbox"
                      :checked="row.selected"
                      @change="row.selected = !row.selected"
                    />
                    <span class="repair-title">{{ row.title || `Audiobook #${row.id}` }}</span>
                  </span>
                  <span class="repair-header-right">
                    <span class="repair-meta">
                      {{ row.authors.join(', ') || 'Unknown author' }}
                      <template v-if="row.currentAsin">
                        · {{ row.currentAsin }}
                        <template v-if="row.currentRegion"> ({{ row.currentRegion }})</template>
                      </template>
                    </span>
                    <button
                      type="button"
                      class="btn btn-secondary repair-revert-btn"
                      title="Revert"
                      aria-label="Revert to the values before your changes"
                      data-testid="repair-revert"
                      :disabled="!isEdited(row)"
                      @click.prevent.stop="revert(row)"
                    >
                      <PhArrowCounterClockwise :size="14" />
                    </button>
                  </span>
                </label>

                <div class="repair-fields">
                  <label class="repair-field repair-field-asin">
                    <span class="repair-field-label">ASIN</span>
                    <input
                      v-model.trim="row.asin"
                      class="form-input"
                      maxlength="10"
                      data-testid="repair-asin"
                      @input="onTyped(row)"
                    />
                  </label>
                  <label class="repair-field">
                    <span class="repair-field-label">Title</span>
                    <input
                      v-model="row.query"
                      class="form-input"
                      data-testid="repair-title"
                      @input="onSearchChanged(row)"
                      @keydown.enter.prevent="search(row)"
                    />
                  </label>
                  <label class="repair-field">
                    <span class="repair-field-label">Author</span>
                    <input
                      v-model="row.author"
                      class="form-input"
                      data-testid="repair-author"
                      @input="onSearchChanged(row)"
                      @keydown.enter.prevent="search(row)"
                    />
                  </label>
                  <label class="repair-field">
                    <span class="repair-field-label">Region</span>
                    <select
                      v-model="row.region"
                      class="form-input"
                      data-testid="repair-region"
                      @change="onSearchChanged(row)"
                    >
                      <option v-for="o in regionOptionsFor(row)" :key="o.value" :value="o.value">
                        {{ o.label }}
                      </option>
                    </select>
                  </label>
                  <button
                    v-if="row.search.status === 'searching'"
                    type="button"
                    class="btn btn-danger repair-search-btn"
                    data-testid="repair-cancel-search"
                    @click="cancelSearch(row)"
                  >
                    <PhX :size="16" />
                    Cancel search
                  </button>
                  <button
                    v-else
                    type="button"
                    class="btn btn-secondary repair-search-btn"
                    data-testid="repair-search"
                    :disabled="!canSearch(row)"
                    :title="searchingRowId !== null ? 'Another book is being searched' : undefined"
                    @click="search(row)"
                  >
                    <PhMagnifyingGlass :size="16" />
                    Search
                  </button>
                </div>

                <!-- The search under the book: pending, failed with Retry, or its results to pick from
                     (the same results list as Library Import's Find Match) -->
                <p
                  v-if="row.search.status === 'searching'"
                  class="repair-search-state"
                  data-testid="repair-search-pending"
                >
                  <PhSpinner class="ph-spin" :size="16" />
                  Searching "{{ row.query.trim() }}" in {{ regionLabel(row.region) }}…
                </p>
                <p
                  v-else-if="row.search.status === 'failed'"
                  class="repair-search-state repair-search-error"
                  data-testid="repair-search-error"
                >
                  <PhWarningCircle :size="16" />
                  <span>{{ row.search.error }}</span>
                  <button
                    type="button"
                    class="btn btn-secondary repair-retry-btn"
                    data-testid="repair-search-retry"
                    :disabled="!canSearch(row)"
                    @click="search(row)"
                  >
                    <PhArrowCounterClockwise :size="14" />
                    Retry
                  </button>
                </p>
                <template v-else-if="row.search.status === 'done'">
                  <FindMatchResults
                    v-if="row.search.results.length"
                    class="repair-search-results"
                    :results="row.search.results"
                    @select="(result) => pick(row, result)"
                  />
                  <p v-else class="repair-search-state" data-testid="repair-search-empty">
                    No results for "{{ row.query.trim() }}"{{
                      row.author.trim() ? ` by "${row.author.trim()}"` : ''
                    }}
                    in {{ regionLabel(row.region) }}.
                  </p>
                </template>
                <p v-if="row.pickedTitle" class="repair-picked" data-testid="repair-picked">
                  New record: {{ row.pickedTitle }}
                </p>
                <p
                  v-if="row.selected && asinChanged(row) && !asinValid(row)"
                  class="repair-hint"
                  data-testid="repair-invalid-asin"
                >
                  {{
                    row.asin.trim()
                      ? 'An ASIN is 10 letters or digits: type it, or search the book.'
                      : 'No ASIN: search the book and pick it, or type its ASIN.'
                  }}
                </p>
              </div>
            </div>
          </div>
        </div>

        <div v-if="finished && results.length" class="form-group repair-results-group">
          <label class="form-label">
            <PhCheckCircle />
            Results
          </label>
          <div class="form-control-card results-list">
            <div
              v-for="result in results"
              :key="result.id"
              class="result-row"
              :class="{ success: result.success, error: !result.success }"
            >
              <component
                :is="result.success ? PhCheckCircle : PhWarningCircle"
                class="result-icon"
              />
              <span class="result-title">{{ result.title }}</span>
              <span class="result-detail">{{ result.detail }}</span>
            </div>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button
            type="button"
            class="btn cancel-button"
            :disabled="executing"
            @click="handleClose"
          >
            <PhX :size="16" />
            {{ finished ? 'Close' : 'Cancel' }}
          </button>
        </template>
        <template #default>
          <button
            v-if="!finished"
            type="button"
            class="btn btn-primary"
            data-testid="repair-confirm"
            :disabled="
              loading ||
              executing ||
              searchingRowId !== null ||
              selectedCount === 0 ||
              selectedRows.some(isInvalid)
            "
            @click="repair"
          >
            <PhSpinner v-if="executing" class="ph-spin" :size="16" />
            <PhWrench v-else :size="16" />
            {{ executing ? 'Repairing…' : `Repair ${selectedCount}` }}
          </button>
          <template v-else>
            <button
              v-if="repairedIds.length"
              type="button"
              class="btn btn-secondary"
              data-testid="repair-organize"
              @click="emit('organize', repairedIds)"
            >
              <PhFolderOpen :size="16" />
              Organize repaired
            </button>
            <button type="button" class="btn btn-primary" @click="handleDone">
              <PhCheckCircle :size="16" />
              Done
            </button>
          </template>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import {
  PhArrowCounterClockwise,
  PhCheckCircle,
  PhCheckSquare,
  PhFolderOpen,
  PhInfo,
  PhMagnifyingGlass,
  PhSpinner,
  PhWarningCircle,
  PhWrench,
  PhX,
} from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/feedback'
import { apiService } from '@/services/api'
import { useLibraryImportStore } from '@/stores/libraryImport'
import { orderedSearchRegionOptions, searchRegionOptions } from '@/utils/languageMapping'
import FindMatchResults from '@/components/domain/audiobook/FindMatchResults.vue'
import type {
  AudiobookExternalIdentifier,
  AudiobookExternalIdentifierInput,
  SearchResult,
} from '@/types'

// As in Library Import's Find Match
const ASIN_PATTERN = /^[A-Z0-9]{10}$/i
// Catalogue offered after the book's and the configured ones
const FALLBACK_REGION = 'us'

interface RowSearch {
  status: 'idle' | 'searching' | 'done' | 'failed'
  results: SearchResult[]
  error: string | null
}

interface RepairRow {
  id: number
  title: string
  authors: string[]
  currentAsin: string
  currentRegion: string
  identifiers: AudiobookExternalIdentifier[]
  asin: string
  region: string
  // What the search looks for: the book's title and first author, editable
  query: string
  author: string
  // The search under the book: its state, results or error
  search: RowSearch
  // The record picked in the search (its ASIN and region are known to exist); null when typed
  pickedTitle: string | null
  selected: boolean
  // Values when the modal opened, restored by Revert
  initial: { asin: string; region: string; query: string; author: string }
}

interface RepairResult {
  id: number
  title: string
  success: boolean
  detail: string
}

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
  organize: [ids: number[]]
}>()

// The search Find Match uses and the configured region live in the Library Import store
const libraryImportStore = useLibraryImportStore()
const configuredRegion = ref(FALLBACK_REGION)
const loading = ref(false)
const executing = ref(false)
const finished = ref(false)
const error = ref<string | null>(null)
const rows = ref<RepairRow[]>([])
const results = ref<RepairResult[]>([])
const progress = ref(0)
// One book is searched at a time: the others wait for it
const searchingRowId = ref<number | null>(null)
const searchControllers = new Map<number, AbortController>()

const selectedRows = computed(() => rows.value.filter((r) => r.selected))
const selectedCount = computed(() => selectedRows.value.length)
const repairedIds = computed(() => results.value.filter((r) => r.success).map((r) => r.id))

// Every Audible region stays available, ordered by usefulness: the book's current region first,
// then the configured one and the US, then the others alphabetically.
function regionOptionsFor(row: RepairRow) {
  return orderedSearchRegionOptions([row.initial.region, configuredRegion.value, FALLBACK_REGION])
}

function regionLabel(region: string): string {
  return searchRegionOptions.find((o) => o.value === region)?.label ?? region
}

function primaryAsin(identifiers: AudiobookExternalIdentifier[]) {
  const asins = identifiers.filter((i) => i.type === 'Asin')
  return asins.find((i) => i.isPrimary) ?? asins[0]
}

async function load() {
  loading.value = true
  finished.value = false
  error.value = null
  results.value = []
  rows.value = []
  try {
    configuredRegion.value = await libraryImportStore.configuredRegion()
    rows.value = await Promise.all(
      props.audiobookIds.map(async (id) => {
        const [book, ids] = await Promise.all([
          apiService.getAudiobook(id),
          apiService.getAudiobookIdentifiers(id),
        ])
        const asin = primaryAsin(ids.identifiers ?? [])
        const initial = {
          asin: asin?.value ?? book.asin ?? '',
          region: asin?.region || configuredRegion.value,
          query: book.title ?? '',
          author: book.authors?.[0] ?? '',
        }
        return {
          id,
          title: book.title ?? '',
          authors: book.authors ?? [],
          currentAsin: asin?.value ?? book.asin ?? '',
          currentRegion: asin?.region ?? '',
          identifiers: ids.identifiers ?? [],
          ...initial,
          pickedTitle: null,
          search: idleSearch(),
          initial,
          selected: true,
        }
      }),
    )
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load the selected audiobooks'
  } finally {
    loading.value = false
  }
}

// A search slower than this is stopped and reported, with Retry
const SEARCH_TIMEOUT_MS = 30_000

function idleSearch(): RowSearch {
  return { status: 'idle', results: [], error: null }
}

function canSearch(row: RepairRow): boolean {
  return searchingRowId.value === null && !!row.query.trim()
}

// Search the title and author (or an ASIN typed as the title) in the row's region, as Library
// Import's Find Match does; the results open under the book
async function search(row: RepairRow) {
  if (!canSearch(row)) return
  const query = row.query.trim()
  const controller = new AbortController()
  let timedOut = false
  const timer = setTimeout(() => {
    timedOut = true
    controller.abort()
  }, SEARCH_TIMEOUT_MS)
  searchControllers.set(row.id, controller)
  searchingRowId.value = row.id
  row.search = { status: 'searching', results: [], error: null }
  try {
    const params = ASIN_PATTERN.test(query)
      ? { asin: query.toUpperCase(), cap: 5 }
      : { title: query, author: row.author.trim() || undefined, cap: 5 }
    const results = await libraryImportStore.searchInRegion(params, row.region, controller.signal)
    row.search = { status: 'done', results, error: null }
  } catch (e) {
    if (controller.signal.aborted && !timedOut) {
      // Canceled by the user: back to the fields, nothing to report
      row.search = idleSearch()
    } else {
      row.search = {
        status: 'failed',
        results: [],
        error: timedOut
          ? `The search took longer than ${SEARCH_TIMEOUT_MS / 1000} seconds and was stopped.`
          : `The search failed: ${(e as Error)?.message ?? 'unknown error'}`,
      }
    }
  } finally {
    clearTimeout(timer)
    searchControllers.delete(row.id)
    if (searchingRowId.value === row.id) searchingRowId.value = null
  }
}

function cancelSearch(row: RepairRow) {
  searchControllers.get(row.id)?.abort()
}

function cancelAllSearches() {
  for (const controller of searchControllers.values()) controller.abort()
}

// The record picked in the results: its ASIN in the region it was found in; the results close
function pick(row: RepairRow, result: SearchResult) {
  if (!result.asin) return
  row.asin = result.asin.toUpperCase()
  row.region = result.region || row.region
  row.pickedTitle = result.title ?? result.asin
  row.selected = true
  row.search = idleSearch()
}

// A typed ASIN is no longer the picked record
function onTyped(row: RepairRow) {
  row.pickedTitle = null
}

// Another title, author or region describes another record: the ASIN no longer matches, so it is
// emptied until the book is searched and picked again (or its ASIN is typed)
function onSearchChanged(row: RepairRow) {
  row.asin = ''
  row.pickedTitle = null
  // Results of the previous title, author or region no longer apply
  if (row.search.status !== 'searching') row.search = idleSearch()
}

function isEdited(row: RepairRow): boolean {
  return (
    row.asin !== row.initial.asin ||
    row.region !== row.initial.region ||
    row.query !== row.initial.query ||
    row.author !== row.initial.author
  )
}

function revert(row: RepairRow) {
  cancelSearch(row)
  Object.assign(row, row.initial)
  row.pickedTitle = null
  row.search = idleSearch()
}

function asinChanged(row: RepairRow): boolean {
  return (
    row.asin.trim().toUpperCase() !== row.currentAsin.toUpperCase() ||
    row.region !== row.currentRegion
  )
}

function asinValid(row: RepairRow): boolean {
  return ASIN_PATTERN.test(row.asin.trim())
}

// Keeping the current ASIN and region only rescans; a new one must look like an ASIN
function isInvalid(row: RepairRow): boolean {
  return asinChanged(row) && !asinValid(row)
}

// A typed ASIN is looked up in its region first, with the same search: a record Audible doesn't
// have there is not saved
async function ensureRecordExists(row: RepairRow) {
  if (row.pickedTitle) return
  const asin = row.asin.trim().toUpperCase()
  const found = await libraryImportStore.searchInRegion({ asin, cap: 5 }, row.region)
  if (!found.some((r) => r.asin?.toUpperCase() === asin))
    throw new Error(
      `Audible ${regionLabel(row.region)} has no record for ${asin}. Search the book and pick it in the right region.`,
    )
}

// The new ASIN becomes the primary one. Identifiers that came from the previous match (provider or
// import) are dropped: an ISBN of the wrong book would let the rescan find it again. Manual ones stay.
function identifiersWithPrimaryAsin(row: RepairRow): AudiobookExternalIdentifierInput[] {
  const asin = row.asin.trim().toUpperCase()
  const manual = row.identifiers
    .filter((i) => i.source === 'Manual')
    .filter((i) => !(i.type === 'Asin' && i.value.toUpperCase() === asin))
    .map((i) => ({
      type: i.type,
      value: i.value,
      region: i.type === 'Asin' ? i.region || null : null,
      isPrimary: i.type === 'Asin' ? false : Boolean(i.isPrimary),
      source: 'Manual' as const,
    }))
  return [
    { type: 'Asin', value: asin, region: row.region, isPrimary: true, source: 'Manual' },
    ...manual,
  ]
}

async function rescan(id: number) {
  try {
    return await apiService.rescanAudiobookMetadata(id)
  } catch (e) {
    // A rescan right after another one for the same book is refused (429): wait and retry once
    if ((e as { status?: number })?.status !== 429) throw e
    await new Promise((resolve) => setTimeout(resolve, 3000))
    return apiService.rescanAudiobookMetadata(id)
  }
}

async function repair() {
  executing.value = true
  progress.value = 0
  results.value = []
  for (const row of selectedRows.value) {
    progress.value++
    try {
      if (asinChanged(row)) {
        if (!asinValid(row)) throw new Error('An ASIN is 10 letters or digits')
        await ensureRecordExists(row)
        await apiService.updateAudiobookIdentifiers(row.id, identifiersWithPrimaryAsin(row))
      }
      const rescanned = await rescan(row.id)
      const book = await apiService.getAudiobook(row.id)
      results.value.push({
        id: row.id,
        title: book.title || row.title,
        success: true,
        detail: `${rescanned.asin ?? row.asin} · ${rescanned.region ?? row.region}`,
      })
    } catch (e) {
      results.value.push({
        id: row.id,
        title: row.title,
        success: false,
        detail: e instanceof Error ? e.message : 'Repair failed',
      })
    }
  }
  executing.value = false
  finished.value = true
}

function selectAll() {
  rows.value.forEach((r) => (r.selected = true))
}

function clearSelection() {
  rows.value.forEach((r) => (r.selected = false))
}

function handleClose() {
  if (executing.value) return
  cancelAllSearches()
  if (finished.value) emit('done')
  else emit('close')
}

function handleDone() {
  emit('done')
}

watch(
  () => props.visible,
  (visible) => {
    if (visible) void load()
    else cancelAllSearches()
  },
  { immediate: true },
)

onBeforeUnmount(cancelAllSearches)
</script>

<style scoped>
.repair-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.75rem;
  padding: 2rem 1rem;
  text-align: center;
}

.repair-icon {
  font-size: 2rem;
}

.repair-error {
  color: #ef9a9a;
}

.repair-group {
  margin-bottom: 1rem;
}

.repair-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  flex-wrap: wrap;
}

/* Same info box as the Organize dialog */
.repair-info-section {
  display: flex;
  align-items: flex-start;
  gap: 0.6rem;
  padding: 0.75rem;
  background-color: rgba(52, 152, 219, 0.09);
  border: 1px solid rgba(52, 152, 219, 0.28);
  border-radius: 6px;
  color: #3498db;
}

.repair-info-section p {
  margin: 0;
  font-size: 0.95rem;
  line-height: 1.4;
  color: #ccc;
}

.repair-toolbar-title {
  margin: 0;
  color: #ccc;
}

.toolbar-actions {
  display: flex;
  gap: 0.5rem;
}

.repair-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.repair-header {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  flex-wrap: wrap;
  cursor: pointer;
}

.repair-heading {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 600;
}

.repair-header-right {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.repair-revert-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.75rem;
  height: 1.75rem;
  padding: 0;
}

.repair-meta {
  font-size: 0.8rem;
  opacity: 0.75;
}

/* ASIN, Title, Author, Region and Search always share the whole row */
.repair-fields {
  display: grid;
  grid-template-columns:
    minmax(8rem, 1fr) minmax(0, 2.5fr) minmax(0, 1.5fr) minmax(12rem, 1.3fr)
    auto;
  align-items: end;
  gap: 0.75rem;
  margin-top: 0.75rem;
}

.repair-field {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  min-width: 0;
}

.repair-field .form-input {
  width: 100%;
  box-sizing: border-box;
}

.repair-field-asin input {
  font-family: monospace;
}

@media (max-width: 720px) {
  .repair-fields {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  }
}

.repair-field-label {
  font-size: 0.8rem;
  color: #aaa;
}

/* Search and Cancel search take the same width, so the row doesn't move when they swap */
.repair-search-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  width: 11rem;
  white-space: nowrap;
}

@media (max-width: 720px) {
  .repair-search-btn {
    grid-column: 1 / -1;
    width: auto;
  }
}

.repair-search-state {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin: 0.75rem 0 0;
  font-size: 0.85rem;
  color: #aaa;
}

.repair-search-error {
  color: #ef4444;
}

.repair-retry-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  padding: 0.25rem 0.6rem;
  font-size: 0.8rem;
}

.repair-search-results {
  margin-top: 0.75rem;
}

.repair-picked {
  margin: 0.5rem 0 0;
  font-size: 0.85rem;
  color: #22c55e;
}

.repair-hint {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  color: #ffb74d;
}

.results-list {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.result-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.result-row.success .result-icon {
  color: #81c784;
}

.result-row.error .result-icon {
  color: #ef9a9a;
}

.result-title {
  color: #e0e0e0;
}

.result-detail {
  margin-left: auto;
  color: #aaa;
  font-size: 0.8rem;
  opacity: 0.75;
}
</style>
