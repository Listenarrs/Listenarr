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
      <!-- The step on the title, so the dialog says where the repair is and how much is left -->
      <ModalHeader :title="title" :icon="PhWrench" @close="handleClose" />
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

        <!-- From Repair on: each book's repair as it runs, then how it ended and which ones to
             organize -->
        <RepairResults
          v-else-if="repairStep"
          v-model:selected="organizeIds"
          :outcomes="outcomes"
          :running="executing"
          @retry="retryRepair"
          :min-height="stepOneHeight"
        />

        <!-- The books stay through the repair: each card shows its own progress -->
        <div v-else-if="rows.length" ref="stepOneRef" class="repair-list-wrapper">
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

          <RepairSelection
            :selected="selectedCount"
            :total="rows.length"
            action="repair"
            @select-all="selectAll"
            @clear="clearSelection"
          />

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
                :narrow="legendAbove"
                :placeholders="rows.length === 1"
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
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter
        ref="footerRef"
        class="repair-footer"
        :class="{ 'repair-footer-legend-above': legendAbove || repairStep }"
        :showCancel="false"
      >
        <template #left>
          <!-- Cancel on step 1. Red while the repairs run: it cancels the books still waiting (the
               one being repaired ends first). Once they are over, Back to step 1, loaded afresh
               (the books may have been repaired) -->
          <button
            ref="cancelRef"
            type="button"
            class="btn cancel-button"
            :class="{ 'btn-danger': executing }"
            :disabled="canceling"
            data-testid="repair-cancel"
            @click="executing ? cancelRepair() : finished ? backToMatching() : handleClose()"
          >
            <!-- Every label in one place, only the current one seen: the button keeps its size -->
            <span class="repair-cancel-labels">
              <span
                v-for="label in cancelLabels"
                :key="label.text"
                :class="{ 'repair-cancel-unseen': label.text !== cancelLabel }"
                :aria-hidden="label.text !== cancelLabel"
                :data-testid="label.text === cancelLabel ? 'repair-cancel-label' : undefined"
              >
                <component :is="label.icon" :size="16" :class="{ 'ph-spin': label.spin }" />
                {{ label.text }}
              </span>
            </span>
          </button>
          <!-- Always in sight: between Cancel and the skipped count when the line has room for it,
               on its own line above them otherwise. There from the start, loading included, so
               the footer never changes shape -->
          <MatchLegend
            v-if="!repairStep"
            ref="legendRef"
            :class="{ 'repair-legend-above': legendAbove }"
            :style="legendAbove ? undefined : { marginLeft: `${legendShift}px` }"
          />
          <template v-else>
            <!-- Step 2's bar colours, always on their own line above the buttons: the line of
                 the buttons keeps its room for the warning -->
            <MatchLegend
              :entries="REPAIR_LEGEND"
              class="repair-legend-above"
              data-testid="repair-legend"
            />
            <!-- The warning's place, kept from the start of step 2: between Close and the skipped
                 count when the line has room for its longest text, on its own line above them
                 otherwise. Filled as soon as a book's repair goes wrong (failed, done in part,
                 canceled) -->
            <span
              class="repair-problems"
              :class="{ 'repair-problems-above': problemsAbove }"
              data-testid="repair-problems-slot"
            >
              <template v-if="problemText">
                <PhWarningCircle :size="16" />
                <span data-testid="repair-problems">{{ problemText }}</span>
              </template>
            </span>
            <!-- The longest warning step 2 can show, unseen: what the place is measured with -->
            <span
              ref="problemsSizerRef"
              class="repair-problems repair-problems-sizer"
              aria-hidden="true"
            >
              <PhWarningCircle :size="16" />
              {{ longestProblemText }}
            </span>
          </template>
        </template>
        <template #default>
          <!-- Always shown, 0 included, from the start: a count that appears and disappears would
               move Repair. Once repaired, the books left out of Organize -->
          <span
            ref="countRef"
            class="repair-count"
            :class="{ 'repair-count-wraps': countWraps }"
            data-testid="repair-count"
          >
            {{ skippedText }} skipped
          </span>
          <button
            v-if="!repairStep"
            type="button"
            class="btn btn-primary"
            data-testid="repair-confirm"
            :disabled="loading || searchingRowId !== null || selectedCount === 0"
            @click="repair"
          >
            <PhWrench :size="16" />
            Repair {{ selectedCount }}
          </button>
          <button
            v-else
            type="button"
            class="btn btn-primary"
            data-testid="repair-organize"
            :disabled="executing || !organizeIds.length"
            :title="
              executing
                ? 'Wait for the repairs to end'
                : organizeIds.length
                  ? undefined
                  : 'Tick a repaired book to organize it'
            "
            @click="emit('organize', organizeIds)"
          >
            <PhFolderOpen :size="16" />
            Organize {{ organizeIds.length }}
          </button>
          <!-- Along the dialog's bottom edge: unseen on step 1 (standby, its room kept), grey from
               Repair on, then filled from left to
               right as each book's repair ends, breathing until the last one. Blue, orange as
               soon as a repair goes wrong. Once canceled, the books left out count as over, so it
               fills up when the running one ends -->
          <div
            class="repair-progress"
            :class="{
              'repair-progress-standby': !repairStep,
              'repair-progress-running': executing,
              'repair-progress-problem': !!problemText,
            }"
            role="progressbar"
            aria-label="Repair progress"
            :aria-valuemax="repairTotal"
            :aria-valuenow="repairCompleted"
            data-testid="repair-progress"
          >
            <div class="repair-progress-fill" :style="{ width: `${progressPercent}%` }" />
          </div>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script lang="ts">
// Repair takes three steps, the last in the Organize dialog it opens: its title there
export const REPAIR_ORGANIZE_TITLE = 'Repair Books - Organize - Step 3/3'
</script>

<script setup lang="ts">
import {
  computed,
  nextTick,
  onBeforeUnmount,
  provide,
  ref,
  watch,
  type ComponentPublicInstance,
} from 'vue'
import {
  PhArrowLeft,
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
import RepairResults, { type RepairOutcome } from './RepairResults.vue'
import RepairSelection from './RepairSelection.vue'
import MatchLegend, { type LegendEntry } from '@/components/domain/audiobook/MatchLegend.vue'
import {
  FALLBACK_REGION,
  asinChanged,
  createBookMatch,
  recordFromResult,
  formatDuration,
  matchState,
  SEARCH_CACHE,
  type BookMatch,
  type BookRecord,
  type BookTask,
  type SearchCache,
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
  // Its repair, once started: waiting, running, then how it ended (shown by its card)
  task: BookTask | null
  // Its record when the repair started, the one it is to get (repaired books only), and its record
  // after the repair
  before: BookRecord | null
  planned: BookRecord | null
  after: BookRecord | null
}

const props = withDefaults(
  defineProps<{
    visible?: boolean
    audiobookIds?: number[]
    // Shown again after its step 3 (Back from Organize): as it was left, not loaded afresh
    resume?: boolean
  }>(),
  {
    visible: false,
    audiobookIds: () => [],
    resume: false,
  },
)

const emit = defineEmits<{
  close: []
  done: []
  organize: [ids: number[]]
}>()

// The search Find Match uses and the configured region live in the Library Import store
const libraryImportStore = useLibraryImportStore()
// The searches answered while the dialog is open, shared by its books; emptied each time it opens
const searchCache: SearchCache = new Map()
provide(SEARCH_CACHE, searchCache)
const configuredRegion = ref(FALLBACK_REGION)
const loading = ref(false)
const executing = ref(false)
const finished = ref(false)
// Cancel asked while the repairs run: the books still waiting are left out
const canceling = ref(false)
// A repair saved something since the dialog opened: closing it, even from step 1 after Back,
// refreshes the page behind
const libraryChanged = ref(false)
// The books Cancel left out, until the dialog opens again or a new repair starts
const canceledCount = ref(0)
// Step 2 from Repair on: the repairs as they run, then their results
const repairStep = computed(() => executing.value || finished.value)
// One book: the room step 1 took in the dialog's body, measured on Repair, kept by step 2 so the
// dialog keeps its height
const stepOneRef = ref<HTMLElement | null>(null)
const stepOneHeight = ref<number | null>(null)

// Measures step 1 before it goes; the returned function, called once step 2 shows, keeps its room.
// The body's visible height (capped by its maximum, step 1 often scrolls), less its padding and
// what step 2's footer takes more (its legend on a line of its own)
function keepStepOneRoom(): () => Promise<void> {
  stepOneHeight.value = null
  const body = stepOneRef.value?.closest<HTMLElement>('.repair-modal-body')
  const footer = footerRef.value?.$el as HTMLElement | undefined
  if (rows.value.length !== 1 || !body || !footer) return async () => {}
  const style = getComputedStyle(body)
  const room = body.clientHeight - px(style.paddingTop) - px(style.paddingBottom)
  const footerBefore = footer.offsetHeight
  return async () => {
    await nextTick()
    const kept = room - (footer.offsetHeight - footerBefore)
    stepOneHeight.value = kept > 0 ? kept : null
  }
}
const error = ref<string | null>(null)
const rows = ref<RepairRow[]>([])
// The repairs over (whatever their outcome), of how many: the progress bar
const repairCompleted = ref(0)
const repairTotal = ref(0)
const progressPercent = computed(() =>
  repairTotal.value ? (repairCompleted.value / repairTotal.value) * 100 : 0,
)
// The repaired books to organize: all of them at first
const organizeIds = ref<number[]>([])
// One book is searched at a time: the others wait for it
const searchingRowId = ref<number | null>(null)

// Steps 1 and 2 of three: pick each book's new record, then the repair process and its results,
// to choose what to organize (step 3, in the Organize dialog)
const title = computed(() =>
  repairStep.value
    ? 'Repair Books - Repair process - Step 2/3'
    : 'Repair Books - Matching - Step 1/3',
)

// The legend stays in the footer, in sight however long the list is: between Cancel and the
// skipped count when the line has room for all of them, on its own line above them otherwise.
// Wrapping alone would put it under them, hence the measure. Centred on the dialog either way: on
// the buttons' line, moved towards the side with more room only as far as the buttons need.
const footerRef = ref<ComponentPublicInstance | null>(null)
const cancelRef = ref<HTMLElement | null>(null)
const legendRef = ref<ComponentPublicInstance | null>(null)
const legendAbove = ref(false)
// How far the legend on the buttons' line stands from Cancel, to be centred on the dialog
const legendShift = ref(0)
// Step 2: the warning's place on its own line, above the buttons
const problemsSizerRef = ref<HTMLElement | null>(null)
const problemsAbove = ref(false)
const countRef = ref<HTMLElement | null>(null)
// The skipped count on two lines, the last resort: even Cancel, it and Repair don't fit one line
const countWraps = ref(false)
let footerObserver: ResizeObserver | null = null

// A length in pixels, 0 when the style has none
const px = (value: string) => parseFloat(value) || 0
const gapOf = (element: Element) => px(getComputedStyle(element).columnGap)

// The width of an element's text on one line, even while it is wrapped: the sum of its lines
function oneLineWidth(element: HTMLElement): number {
  const range = document.createRange()
  range.selectNodeContents(element)
  const lines = typeof range.getClientRects === 'function' ? Array.from(range.getClientRects()) : []
  return lines.length ? lines.reduce((width, line) => width + line.width, 0) : element.offsetWidth
}

function placeLegend() {
  const footer = footerRef.value?.$el as HTMLElement | undefined
  const legend = legendRef.value?.$el as HTMLElement | undefined
  const left = footer?.querySelector<HTMLElement>('.modal-footer-left')
  const actions = footer?.querySelector<HTMLElement>('.modal-footer-actions')
  if (!footer || !left || !actions || !cancelRef.value) return
  const footerStyle = getComputedStyle(footer)
  const room = footer.clientWidth - px(footerStyle.paddingLeft) - px(footerStyle.paddingRight)
  // The legend's width on one line, whatever line it is on now (none once repaired)
  const entries = legend ? (Array.from(legend.children) as HTMLElement[]) : []
  const legendWidth =
    entries.reduce((width, entry) => width + entry.offsetWidth, 0) +
    (legend ? gapOf(legend) : 0) * Math.max(entries.length - 1, 0)
  // The skipped count and Repair, the count on one line whatever it shows now
  const count = countRef.value
  const actionsWidth = count
    ? actions.offsetWidth - count.offsetWidth + oneLineWidth(count)
    : actions.offsetWidth
  const cancelAndActions = cancelRef.value.offsetWidth + gapOf(footer) + actionsWidth
  legendAbove.value = !!legend && cancelAndActions + gapOf(left) + legendWidth > room
  // Its left edge, from the footer's content box: centred, but between Cancel and the skipped count
  const nearest = cancelRef.value.offsetWidth + gapOf(left)
  const farthest = room - actionsWidth - gapOf(footer) - legendWidth
  legendShift.value = legendAbove.value
    ? 0
    : Math.max(0, Math.min((room - legendWidth) / 2, farthest) - nearest)
  const sizer = problemsSizerRef.value
  problemsAbove.value = !!sizer && cancelAndActions + gapOf(footer) + sizer.offsetWidth > room
  countWraps.value = cancelAndActions > room
}

// Measured again when the modal resizes, the skipped count or the button changes, or the legend
// or the warning's longest text shows or goes
watch(
  [footerRef, legendRef, problemsSizerRef],
  ([footer, legend, sizer]) => {
    footerObserver?.disconnect()
    footerObserver = null
    if (!footer || typeof ResizeObserver !== 'function') return
    footerObserver = new ResizeObserver(placeLegend)
    const footerElement = footer.$el as HTMLElement
    footerObserver.observe(footerElement)
    if (legend) footerObserver.observe(legend.$el as HTMLElement)
    if (sizer) footerObserver.observe(sizer)
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
// Matching: the books left out of the repair. Step 2 only holds the books to repair: while they
// are repaired, the ones Cancel left out; once over, the ones left out of Organize
const skippedText = computed(() => {
  if (finished.value) return repairTotal.value - organizeIds.value.length
  if (executing.value) return canceledCount.value
  return skippedCount.value
})
// What the bar along the bottom of each book means on step 2
const REPAIR_LEGEND: LegendEntry[] = [
  { tone: 'idle', label: 'Waiting or canceled' },
  { tone: 'progress', label: 'Repairing' },
  { tone: 'success', label: 'Repaired' },
  { tone: 'warning', label: 'Repaired in part' },
  { tone: 'error', label: 'Failed' },
]

// The longest warning the books on step 2 can bring: every kind, each counting them all
const longestProblemText = computed(() => {
  const n = repairTotal.value
  return `Not all repaired: ${n} failed, ${n} repaired in part, ${n} canceled`
})

// What went wrong on step 2, counted by kind: the footer's warning, and its bar orange
const problemText = computed(() => {
  const count = (state: string) => rows.value.filter((row) => row.task?.state === state).length
  const parts = [
    [count('failed'), 'failed'],
    [count('warning'), 'repaired in part'],
    [canceledCount.value, 'canceled'],
  ]
    .filter(([n]) => n)
    .map(([n, what]) => `${n} ${what}`)
  return parts.length ? `Not all repaired: ${parts.join(', ')}` : null
})
// Step 2: the books to repair only, the unticked or unmatched ones stay behind on step 1
const outcomes = computed<RepairOutcome[]>(() =>
  rows.value
    .filter((row): row is RepairRow & { planned: BookRecord } => !!row.planned)
    .map((row) => ({
      id: row.id,
      title: row.title,
      state: row.task?.state ?? 'skipped',
      message: row.task?.text ?? '',
      before: row.before ?? {},
      planned: row.planned,
      after: row.after,
    })),
)

function primaryAsin(identifiers: AudiobookExternalIdentifier[]) {
  const asins = identifiers.filter((i) => i.type === 'Asin')
  return asins.find((i) => i.isPrimary) ?? asins[0]
}

async function load() {
  loading.value = true
  finished.value = false
  canceling.value = false
  canceledCount.value = 0
  error.value = null
  rows.value = []
  searchCache.clear()
  organizeIds.value = []
  repairCompleted.value = 0
  repairTotal.value = 0
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
          task: null,
          before: null,
          planned: null,
          after: null,
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

// Step 2 shows the ticked books' repairs: they wait their turn, the others aren't on it.
// The repairs run one after the other; each card then says how its repair ended and how long it
// took, and shows the book as it is now. Cancel leaves out the books still waiting
async function repair() {
  const toRepair = [...selectedRows.value]
  const keepRoom = keepStepOneRoom()
  executing.value = true
  canceling.value = false
  canceledCount.value = 0
  organizeIds.value = []
  repairCompleted.value = 0
  repairTotal.value = toRepair.length
  for (const row of rows.value) {
    row.before = {
      title: row.title,
      subtitle: row.subtitle,
      authors: row.authors,
      asin: row.match.current.asin,
      region: row.match.current.region,
    }
    const picked = toRepair.includes(row) ? row.match.picked : null
    row.planned = picked ? recordFromResult(picked, row.match.region) : null
    row.task = picked ? { state: 'waiting', text: 'Waiting to be repaired' } : null
  }
  await keepRoom()
  for (const row of toRepair) {
    // Canceled while it waited
    if (row.task?.state !== 'waiting') continue
    await repairOne(row)
    repairCompleted.value++
  }
  executing.value = false
  canceling.value = false
  finished.value = true
}

// One book's repair: its new record saved (when its ASIN or region changes), then its metadata
// rescanned and the book read again. Its row says where it is, then how it ended
async function repairOne(row: RepairRow) {
  row.task = { state: 'running', text: 'Repairing…' }
  const startedAt = performance.now()
  const took = () => formatDuration(performance.now() - startedAt)
  let saved = false
  try {
    // The new record was looked up in its region: saved, then the metadata read from it
    if (asinChanged(row.match)) {
      await apiService.updateAudiobookIdentifiers(row.id, identifiersWithPrimaryAsin(row))
      saved = true
      libraryChanged.value = true
    }
    const rescanned = await rescan(row.id)
    const book = await apiService.getAudiobook(row.id)
    const asin = rescanned.asin ?? row.match.picked?.asin ?? ''
    const region = rescanned.region ?? row.match.picked?.region ?? row.match.region
    row.title = book.title || row.title
    row.subtitle = book.subtitle ?? row.subtitle
    row.authors = book.authors ?? row.authors
    row.match.current = { asin, region }
    row.after = { title: row.title, subtitle: row.subtitle, authors: row.authors, asin, region }
    // The record it now points at follows, drawn like New (step 2)
    row.task = { state: 'done', text: `Repaired in ${took()}` }
    libraryChanged.value = true
    // Ticked for Organize as soon as it is repaired
    organizeIds.value = [...organizeIds.value, row.id]
  } catch (e) {
    const message = e instanceof Error ? e.message : 'Repair failed'
    // The new ASIN saved but its metadata not read: repaired in part
    if (saved)
      row.after = {
        ...row.before,
        asin: row.match.picked?.asin ?? '',
        region: row.match.picked?.region || row.match.region,
      }
    row.task = saved
      ? {
          state: 'warning',
          text: `The new ASIN is saved, but the metadata rescan failed (${took()}): ${message}`,
        }
      : { state: 'failed', text: `Repair failed (${took()}): ${message}` }
  }
}

// Retry on a book whose repair failed or was done in part, once the repairs are over: that book
// repaired again, the dialog repairing meanwhile (red Cancel, Organize waiting)
async function retryRepair(id: number) {
  const row = rows.value.find((r) => r.id === id)
  if (!row || executing.value || (row.task?.state !== 'failed' && row.task?.state !== 'warning'))
    return
  executing.value = true
  await repairOne(row)
  executing.value = false
  canceling.value = false
}

// The books still waiting are left out at once; the one being repaired ends first (its request
// can't be taken back)
function cancelRepair() {
  if (!executing.value) return
  canceling.value = true
  for (const row of rows.value)
    if (row.task?.state === 'waiting') {
      row.task = { state: 'skipped', text: 'Canceled: not repaired' }
      canceledCount.value++
      repairCompleted.value++
    }
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
  if (finished.value || libraryChanged.value) emit('done')
  else emit('close')
}

// The footer's left button: Cancel, Canceling… while the running repair ends, Back once they are
// over
const cancelLabels = [
  { text: 'Cancel', icon: PhX, spin: false },
  { text: 'Canceling…', icon: PhSpinner, spin: true },
  { text: 'Back', icon: PhArrowLeft, spin: false },
]
const cancelLabel = computed(() =>
  canceling.value ? 'Canceling…' : finished.value && !executing.value ? 'Back' : 'Cancel',
)

// Back from step 2 to step 1, loaded as when the dialog opens: the books show what the repairs
// made of them
function backToMatching() {
  void load()
}

watch(
  () => props.visible,
  (visible) => {
    if (!visible || props.resume) return
    libraryChanged.value = false
    void load()
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

.repair-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

/* Always on one line, unless even Cancel, the count and Repair don't fit one line (measured):
   only then it wraps, centred */
.repair-count {
  font-size: 0.85rem;
  color: #aaa;
  white-space: nowrap;
  text-align: center;
}

.repair-count.repair-count-wraps {
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

.repair-cancel-labels {
  display: grid;
}

.repair-cancel-labels > span {
  grid-area: 1 / 1;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
}

.repair-cancel-unseen {
  visibility: hidden;
}

/* Red while the repairs run: the modal footer's grey Cancel would win over .btn-danger */
.modal-footer.repair-footer .cancel-button.btn-danger {
  background-color: #f44336;
  border-color: #f44336;
}

.modal-footer.repair-footer .cancel-button.btn-danger:hover:not(:disabled) {
  background-color: #e53935;
}

/* The progress bar along the dialog's bottom edge, under the footer */
.modal-footer.repair-footer {
  position: relative;
}

.repair-progress {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 3px;
  background: var(--status-idle);
  overflow: hidden;
}

.repair-progress-fill {
  height: 100%;
  background: var(--status-progress);
  transition: width 0.3s ease;
}

/* Standby (step 1, no repair yet): unseen, the dialog's own colour, but it keeps its room */
.repair-progress.repair-progress-standby {
  background: transparent;
}

.repair-progress-running .repair-progress-fill {
  animation: repair-progress-breathe 1.6s ease-in-out infinite alternate;
}

.repair-progress-problem .repair-progress-fill {
  background: var(--status-warning);
}

@keyframes repair-progress-breathe {
  from {
    opacity: 0.35;
  }
  to {
    opacity: 1;
  }
}

@media (prefers-reduced-motion: reduce) {
  .repair-progress-running .repair-progress-fill {
    animation: none;
  }
}

.repair-problems {
  flex: 1 1 auto;
  min-width: 0;
  min-height: 1.3em;
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 0.85rem;
  color: var(--status-warning);
}

.repair-legend-above,
.repair-problems-above {
  order: -1;
  flex-basis: 100%;
}

/* Above the buttons, centred on the dialog */
.repair-legend-above {
  justify-content: center;
}

.repair-problems-sizer {
  position: absolute;
  top: 0;
  left: 0;
  visibility: hidden;
  white-space: nowrap;
  pointer-events: none;
  flex: none;
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
</style>
