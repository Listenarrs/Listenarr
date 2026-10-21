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
              <BookMatchCard
                v-for="row in rows"
                :key="row.id"
                v-model:match="row.match"
                :configured-region="configuredRegion"
                :search-locked="searchingRowId !== null && searchingRowId !== row.id"
                :check-asin="row.selected"
                :data-testid="`repair-row-${row.id}`"
                @searching="(searching) => onSearching(row, searching)"
                @picked="row.selected = true"
              >
                <template #heading>
                  <label class="repair-heading">
                    <input
                      type="checkbox"
                      class="repair-checkbox"
                      :checked="row.selected"
                      @change="row.selected = !row.selected"
                    />
                    <span class="repair-title">{{ row.title || `Audiobook #${row.id}` }}</span>
                  </label>
                </template>
                <template #meta>
                  {{ row.authors.join(', ') || 'Unknown author' }}
                  <template v-if="row.match.current.asin">
                    · {{ row.match.current.asin }}
                    <template v-if="row.match.current.region">
                      ({{ row.match.current.region }})</template
                    >
                  </template>
                </template>
              </BookMatchCard>
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
              <span class="result-duration" data-testid="repair-result-duration">
                {{ formatDuration(result.durationMs) }}
              </span>
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
              selectedRows.some((row) => asinInvalid(row.match))
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
import { computed, ref, watch } from 'vue'
import {
  PhCheckCircle,
  PhCheckSquare,
  PhFolderOpen,
  PhInfo,
  PhSpinner,
  PhWarningCircle,
  PhWrench,
  PhX,
} from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/feedback'
import { apiService } from '@/services/api'
import { useLibraryImportStore } from '@/stores/libraryImport'
import BookMatchCard from '@/components/domain/audiobook/BookMatchCard.vue'
import {
  FALLBACK_REGION,
  asinChanged,
  asinInvalid,
  createBookMatch,
  resolveRecord,
  type BookMatch,
} from '@/utils/bookMatch'
import type { AudiobookExternalIdentifier, AudiobookExternalIdentifierInput } from '@/types'

interface RepairRow {
  id: number
  title: string
  authors: string[]
  identifiers: AudiobookExternalIdentifier[]
  // The record the book should point at, edited in its card
  match: BookMatch
  selected: boolean
}

interface RepairResult {
  id: number
  title: string
  success: boolean
  detail: string
  // How long the repair of this book took, from its first request to its last
  durationMs: number
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

const selectedRows = computed(() => rows.value.filter((r) => r.selected))
const selectedCount = computed(() => selectedRows.value.length)
const repairedIds = computed(() => results.value.filter((r) => r.success).map((r) => r.id))

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
  searchingRowId.value = null
  try {
    configuredRegion.value = await libraryImportStore.configuredRegion()
    rows.value = await Promise.all(
      props.audiobookIds.map(async (id) => {
        const [book, ids] = await Promise.all([
          apiService.getAudiobook(id),
          apiService.getAudiobookIdentifiers(id),
        ])
        const asin = primaryAsin(ids.identifiers ?? [])
        const match = createBookMatch(
          {
            asin: asin?.value ?? book.asin ?? '',
            region: asin?.region || configuredRegion.value,
            query: book.title ?? '',
            author: book.authors?.[0] ?? '',
          },
          { asin: asin?.value ?? book.asin ?? '', region: asin?.region ?? '' },
        )
        return {
          id,
          title: book.title ?? '',
          authors: book.authors ?? [],
          identifiers: ids.identifiers ?? [],
          match,
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

function onSearching(row: RepairRow, searching: boolean) {
  if (searching) searchingRowId.value = row.id
  else if (searchingRowId.value === row.id) searchingRowId.value = null
}

// The new ASIN becomes the primary one. Identifiers that came from the previous match (provider or
// import) are dropped: an ISBN of the wrong book would let the rescan find it again. Manual ones stay.
function identifiersWithPrimaryAsin(row: RepairRow): AudiobookExternalIdentifierInput[] {
  const asin = row.match.asin.trim().toUpperCase()
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
    { type: 'Asin', value: asin, region: row.match.region, isPrimary: true, source: 'Manual' },
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
    const startedAt = performance.now()
    try {
      if (asinChanged(row.match)) {
        if (asinInvalid(row.match)) throw new Error('An ASIN is 10 letters or digits')
        // A typed ASIN is checked in its region first, with the same search
        await resolveRecord(row.match, libraryImportStore.searchInRegion)
        await apiService.updateAudiobookIdentifiers(row.id, identifiersWithPrimaryAsin(row))
      }
      const rescanned = await rescan(row.id)
      const book = await apiService.getAudiobook(row.id)
      results.value.push({
        id: row.id,
        title: book.title || row.title,
        success: true,
        detail: `${rescanned.asin ?? row.match.asin} · ${rescanned.region ?? row.match.region}`,
        durationMs: performance.now() - startedAt,
      })
    } catch (e) {
      results.value.push({
        id: row.id,
        title: row.title,
        success: false,
        detail: e instanceof Error ? e.message : 'Repair failed',
        durationMs: performance.now() - startedAt,
      })
    }
  }
  executing.value = false
  finished.value = true
}

// In seconds, with up to 2 decimals: 1.23 s, 1.5 s, 2 s
function formatDuration(durationMs: number) {
  return `${Number((durationMs / 1000).toFixed(2))} s`
}

function selectAll() {
  rows.value.forEach((r) => (r.selected = true))
}

function clearSelection() {
  rows.value.forEach((r) => (r.selected = false))
}

// Closing stops the running search: the cards go away with the modal
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

.repair-heading {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
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

.result-duration {
  min-width: 4rem;
  text-align: right;
  color: #aaa;
  font-size: 0.8rem;
  font-variant-numeric: tabular-nums;
}
</style>
