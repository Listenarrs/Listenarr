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
              Point each book at the right Audible record: use Find Match to search its title and
              author in a region and pick the result, or type the ASIN and its region (it is checked
              in that region). Repairing saves the ASIN and its region and rescans the metadata from
              it.
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
                    <span class="repair-field-label">Region</span>
                    <select
                      v-model="row.region"
                      class="form-input"
                      data-testid="repair-region"
                      @change="onTyped(row)"
                    >
                      <option v-for="o in regionOptionsFor(row)" :key="o.value" :value="o.value">
                        {{ o.label }}
                      </option>
                    </select>
                  </label>
                  <button
                    type="button"
                    class="btn btn-secondary repair-find-btn"
                    title="Find Match"
                    data-testid="repair-find-match"
                    @click="findMatchRow = row"
                  >
                    <PhMagnifyingGlass :size="16" />
                    Find Match
                  </button>
                </div>
                <p v-if="row.pickedTitle" class="repair-picked" data-testid="repair-picked">
                  New record: {{ row.pickedTitle }}
                </p>
                <p
                  v-if="row.selected && asinChanged(row) && !asinValid(row)"
                  class="repair-hint"
                  data-testid="repair-invalid-asin"
                >
                  An ASIN is 10 letters or digits: type it, or use Find Match.
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
            :disabled="loading || executing || selectedCount === 0 || selectedRows.some(isInvalid)"
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

  <!-- Canary's Find Match, searching the book's title and author in its region -->
  <LibraryImportSearchModal
    v-if="findMatchRow"
    :target="findMatchTarget(findMatchRow)"
    @close="findMatchRow = null"
    @select="(result) => pick(findMatchRow!, result)"
  />
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
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
import LibraryImportSearchModal from '@/components/domain/audiobook/LibraryImportSearchModal.vue'
import type {
  AudiobookExternalIdentifier,
  AudiobookExternalIdentifierInput,
  FindMatchTarget,
  SearchResult,
} from '@/types'

// As in Find Match
const ASIN_PATTERN = /^[A-Z0-9]{10}$/i
// Catalogue offered after the book's and the configured ones
const FALLBACK_REGION = 'us'

interface RepairRow {
  id: number
  title: string
  authors: string[]
  currentAsin: string
  currentRegion: string
  identifiers: AudiobookExternalIdentifier[]
  asin: string
  region: string
  // The record picked in Find Match (its ASIN and region are known to exist); null when typed
  pickedTitle: string | null
  selected: boolean
  // Values when the modal opened, restored by Revert
  initial: { asin: string; region: string }
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

// Find Match's search and configured region live in the Library Import store
const libraryImportStore = useLibraryImportStore()
const configuredRegion = ref(FALLBACK_REGION)
const loading = ref(false)
const executing = ref(false)
const finished = ref(false)
const error = ref<string | null>(null)
const rows = ref<RepairRow[]>([])
const results = ref<RepairResult[]>([])
const progress = ref(0)
const findMatchRow = ref<RepairRow | null>(null)

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

function findMatchTarget(row: RepairRow): FindMatchTarget {
  return { name: row.title, query: row.title, author: row.authors[0] ?? '', region: row.region }
}

// The record picked in Find Match: its ASIN in the region it was found in
function pick(row: RepairRow, result: SearchResult) {
  if (!result.asin) return
  row.asin = result.asin.toUpperCase()
  row.region = result.region || row.region
  row.pickedTitle = result.title ?? result.asin
  row.selected = true
}

// A typed ASIN or region is no longer the picked record
function onTyped(row: RepairRow) {
  row.pickedTitle = null
}

function isEdited(row: RepairRow): boolean {
  return row.asin !== row.initial.asin || row.region !== row.initial.region
}

function revert(row: RepairRow) {
  row.asin = row.initial.asin
  row.region = row.initial.region
  row.pickedTitle = null
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

// A typed ASIN is looked up in its region first, with Find Match's search: a record Audible doesn't
// have there is not saved
async function ensureRecordExists(row: RepairRow) {
  if (row.pickedTitle) return
  const asin = row.asin.trim().toUpperCase()
  const found = await libraryImportStore.searchInRegion({ asin, cap: 5 }, row.region)
  if (!found.some((r) => r.asin?.toUpperCase() === asin))
    throw new Error(
      `Audible ${regionLabel(row.region)} has no record for ${asin}. Use Find Match to pick the book in the right region.`,
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
  },
  { immediate: true },
)
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

.repair-fields {
  display: flex;
  align-items: flex-end;
  flex-wrap: wrap;
  gap: 0.75rem;
  margin-top: 0.75rem;
}

.repair-field {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.repair-field-asin input {
  width: 9.5rem;
  font-family: monospace;
}

.repair-field-label {
  font-size: 0.8rem;
  color: #aaa;
}

.repair-find-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
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
