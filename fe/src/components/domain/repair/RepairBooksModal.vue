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
            <div class="repair-info-lines">
              <p>Find the right Audible record for each book, then repair.</p>
              <p data-testid="repair-info-asin">
                A filled ASIN is searched on its own: the title and author are left out of that
                search.
              </p>
            </div>
          </div>

          <div class="form-group repair-group">
            <label class="form-label">
              <PhCheckSquare />
              Selection
            </label>
            <div class="form-control-card">
              <div class="repair-toolbar">
                <p class="repair-toolbar-title">
                  <strong>{{ selectedCount }}</strong> of {{ rows.length }} book(s) to repair
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
                :excluded="!row.selected"
                :book="{ title: row.title, subtitle: row.subtitle, authors: row.authors }"
                :data-testid="`repair-row-${row.id}`"
                @searching="(searching) => onSearching(row, searching)"
                @matched="row.selected = true"
              >
                <template #heading>
                  <label
                    class="repair-heading"
                    :title="isMatched(row) ? undefined : 'Find its new record first'"
                  >
                    <input
                      type="checkbox"
                      class="repair-checkbox"
                      data-testid="repair-include"
                      :checked="isIncluded(row)"
                      :disabled="!isMatched(row)"
                      @change="row.selected = !row.selected"
                    />
                    <span class="repair-title">{{ row.title || `Audiobook #${row.id}` }}</span>
                  </label>
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
      <ModalFooter
        ref="footerRef"
        class="repair-footer"
        :class="{ 'repair-footer-legend-above': legendAbove }"
        :showCancel="false"
      >
        <template #left>
          <button
            ref="cancelRef"
            type="button"
            class="btn cancel-button"
            :disabled="executing"
            @click="handleClose"
          >
            <PhX :size="16" />
            {{ finished ? 'Close' : 'Cancel' }}
          </button>
          <!-- Always in sight: between Cancel and the skipped count when the line has room for it,
               on its own line above them otherwise -->
          <MatchLegend
            v-if="!finished && rows.length"
            ref="legendRef"
            :class="{ 'repair-legend-above': legendAbove }"
          />
        </template>
        <template #default>
          <span
            v-if="!finished && !loading && rows.length"
            class="repair-count"
            data-testid="repair-count"
          >
            {{ skippedCount ? `${skippedCount} skipped` : '' }}
          </span>
          <button
            v-if="!finished"
            type="button"
            class="btn btn-primary"
            data-testid="repair-confirm"
            :disabled="loading || executing || searchingRowId !== null || selectedCount === 0"
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
import { computed, onBeforeUnmount, ref, watch, type ComponentPublicInstance } from 'vue'
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
import MatchLegend from '@/components/domain/audiobook/MatchLegend.vue'
import {
  FALLBACK_REGION,
  asinChanged,
  createBookMatch,
  matchState,
  type BookMatch,
} from '@/utils/bookMatch'
import type { AudiobookExternalIdentifier, AudiobookExternalIdentifierInput } from '@/types'

interface RepairRow {
  id: number
  title: string
  subtitle: string
  authors: string[]
  identifiers: AudiobookExternalIdentifier[]
  // The record the book should point at, edited in its card
  match: BookMatch
  // Ticked: repaired once it has a new record (finding one ticks it)
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

// The legend stays in the footer, in sight however long the list is: between Cancel and the
// skipped count when the line has room for all of them, on its own line above them otherwise.
// Wrapping alone would put it under them, hence the measure.
const footerRef = ref<ComponentPublicInstance | null>(null)
const cancelRef = ref<HTMLElement | null>(null)
const legendRef = ref<ComponentPublicInstance | null>(null)
const legendAbove = ref(false)
let footerObserver: ResizeObserver | null = null

// A length in pixels, 0 when the style has none
const px = (value: string) => parseFloat(value) || 0
const gapOf = (element: Element) => px(getComputedStyle(element).columnGap)

function placeLegend() {
  const footer = footerRef.value?.$el as HTMLElement | undefined
  const legend = legendRef.value?.$el as HTMLElement | undefined
  const left = footer?.querySelector<HTMLElement>('.modal-footer-left')
  const actions = footer?.querySelector<HTMLElement>('.modal-footer-actions')
  if (!footer || !legend || !left || !actions || !cancelRef.value) return
  const footerStyle = getComputedStyle(footer)
  const room = footer.clientWidth - px(footerStyle.paddingLeft) - px(footerStyle.paddingRight)
  // The legend's width on one line, whatever line it is on now
  const entries = Array.from(legend.children) as HTMLElement[]
  const legendWidth =
    entries.reduce((width, entry) => width + entry.offsetWidth, 0) +
    gapOf(legend) * Math.max(entries.length - 1, 0)
  const needed =
    cancelRef.value.offsetWidth + gapOf(left) + legendWidth + gapOf(footer) + actions.offsetWidth
  legendAbove.value = needed > room
}

// Measured again when the modal resizes, the skipped count changes, or the legend shows
watch(
  [footerRef, legendRef],
  ([footer, legend]) => {
    footerObserver?.disconnect()
    footerObserver = null
    if (!footer || !legend || typeof ResizeObserver !== 'function') return
    footerObserver = new ResizeObserver(placeLegend)
    const footerElement = footer.$el as HTMLElement
    footerObserver.observe(footerElement)
    footerObserver.observe(legend.$el as HTMLElement)
    const actions = footerElement.querySelector('.modal-footer-actions')
    if (actions) footerObserver.observe(actions)
  },
  { flush: 'post' },
)
onBeforeUnmount(() => footerObserver?.disconnect())

// Only a book with a new record, looked up in its region, is repaired
const selectedRows = computed(() => rows.value.filter(isIncluded))
const selectedCount = computed(() => selectedRows.value.length)
const skippedCount = computed(() => rows.value.length - selectedCount.value)
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
          subtitle: book.subtitle ?? '',
          authors: book.authors ?? [],
          identifiers: ids.identifiers ?? [],
          match,
          selected: false,
        }
      }),
    )
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load the selected audiobooks'
  } finally {
    loading.value = false
  }
}

function isMatched(row: RepairRow): boolean {
  return matchState(row.match) === 'matched'
}

function isIncluded(row: RepairRow): boolean {
  return row.selected && isMatched(row)
}

function onSearching(row: RepairRow, searching: boolean) {
  if (searching) searchingRowId.value = row.id
  else if (searchingRowId.value === row.id) searchingRowId.value = null
}

// The new ASIN becomes the primary one. Identifiers that came from the previous match (provider or
// import) are dropped: an ISBN of the wrong book would let the rescan find it again. Manual ones stay.
function identifiersWithPrimaryAsin(row: RepairRow): AudiobookExternalIdentifierInput[] {
  // Only a matched book is repaired: its new record is the picked one, whatever the fields hold
  const asin = (row.match.picked?.asin ?? '').toUpperCase()
  const region = row.match.picked?.region || row.match.region
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
  return [{ type: 'Asin', value: asin, region, isPrimary: true, source: 'Manual' }, ...manual]
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
      // The new record was looked up in its region: saved, then the metadata read from it
      if (asinChanged(row.match))
        await apiService.updateAudiobookIdentifiers(row.id, identifiersWithPrimaryAsin(row))
      const rescanned = await rescan(row.id)
      const book = await apiService.getAudiobook(row.id)
      results.value.push({
        id: row.id,
        title: book.title || row.title,
        success: true,
        detail: `${rescanned.asin ?? row.match.picked?.asin} · ${rescanned.region ?? row.match.region}`,
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

// Every book with a new record
function selectAll() {
  rows.value.forEach((r) => (r.selected = isMatched(r)))
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

.repair-info-lines {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
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

/* One line while the legend shares the footer line, so it is measured whole: it only wraps,
   centred, once the legend is above and Cancel, the count and Repair still don't fit */
.repair-count {
  font-size: 0.85rem;
  color: #aaa;
  white-space: nowrap;
  text-align: center;
}

.repair-footer-legend-above .repair-count {
  white-space: normal;
}

/* No room for the legend between Cancel and the skipped count: it takes a line of its own, above
   them */
.modal-footer.repair-footer-legend-above {
  flex-wrap: wrap;
  row-gap: 0.6rem;
}

.repair-footer-legend-above :deep(.modal-footer-left) {
  display: contents;
}

/* Cancel keeps its own width wherever the legend is (modal footers stretch their buttons below
   768px), and so does it in the measure. Set on both layouts, so nothing changes when the legend
   moves: a change here would play the footer buttons' transition, Cancel stretching for a moment */
.repair-footer .cancel-button {
  flex: 0 0 auto;
}

.repair-legend-above {
  order: -1;
  flex-basis: 100%;
}

/* On a phone the footer stays one line: Cancel on the left, "n skipped" and Repair on the right
   (the shared footer stacks its buttons in a column there). Only the results' three buttons may
   wrap, when they don't fit */
@media (max-width: 480px) {
  .modal-footer.repair-footer {
    flex-direction: row;
    flex-wrap: wrap;
    align-items: center;
    padding: 0.75rem 1rem;
  }

  .repair-footer :deep(.modal-footer-left) {
    margin-bottom: 0;
  }

  .repair-footer :deep(.modal-footer-actions .btn) {
    width: auto;
  }
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
