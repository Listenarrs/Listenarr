/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { computed, onScopeDispose, ref } from 'vue'
import { apiService } from '@/services/api'
import type { Audiobook } from '@/types'
import { logger } from '@/utils/logger'
import { useLibraryImportStore } from '@/stores/libraryImport'
import {
  FALLBACK_REGION,
  hasAsinChanged,
  formatDuration,
  type BookRecord,
  bookSummary,
  recordFromResult,
  type SearchCache,
} from '@/utils/bookMatch'
import {
  EMPTIED_RECORD_FIELDS,
  identifiersWithPrimaryAsin,
  isIncluded,
  isMatched,
  repairProblemText,
  failedRepairRow,
  repairRowFrom,
  toRepairOutcomes,
  type RepairRow,
} from '@/utils/repair'

// The server rescans a book at most once every 15 s, and 5 times in 10 minutes: a rescan refused
// for coming too soon (429) waits what the server says (its card counting down), then is asked
// once more; a longer wait (the 10 minutes' limit) fails at once, saying when to retry
const RESCAN_COOLDOWN_SECONDS = 15
const MAX_RESCAN_WAIT_SECONDS = 20
const RESCAN_WAIT_MARGIN_MS = 500

// Cancel asked while the book being repaired waits for the rescan cooldown
class RescanWaitCanceled extends Error {}

// About how long until a rescan is allowed again: 40 s, 8 min
function waitText(seconds: number): string {
  return seconds < 60 ? `${seconds} s` : `${Math.ceil(seconds / 60)} min`
}

// Repair Books' books and their repairs: loaded on step 1, each one's new record picked, then
// repaired one after the other on step 2, with Cancel and Retry
export function useRepairBooks(audiobookIds: () => number[]) {
  // The search Find Match uses and the configured region live in the Library Import store
  const libraryImportStore = useLibraryImportStore()
  // The searches answered while the dialog is open, shared by its books; emptied each time it
  // loads
  const searchCache: SearchCache = new Map()
  const configuredRegion = ref(FALLBACK_REGION)
  const isLoading = ref(false)
  const loadError = ref<string | null>(null)
  const repairRows = ref<RepairRow[]>([])
  const isExecuting = ref(false)
  const isFinished = ref(false)
  // Cancel asked while the repairs run: the books still waiting are left out
  const isCanceling = ref(false)
  // The repairs held while the user is asked something (leaving the page): the book being
  // repaired ends, the next one waits until resumeRepair or cancelRepair
  const isPaused = ref(false)
  let resumeWaiters: (() => void)[] = []
  // Ends the rescan cooldown wait of the book being repaired, when it waits (Cancel)
  let stopRescanWait: (() => void) | null = null
  // A repair saved something since the dialog opened: closing it, even from step 1 after Back,
  // refreshes the page behind
  const hasLibraryChanged = ref(false)
  // The books Cancel left out, until the dialog loads again or a new repair starts
  const canceledCount = ref(0)
  // The repairs over (whatever their outcome), of how many: the progress bar
  const completedRepairCount = ref(0)
  const totalRepairCount = ref(0)
  // The repaired books to organize: all of them at first
  const audiobookIdsToOrganize = ref<number[]>([])
  // One book is searched at a time: the others wait for it
  const searchingAudiobookId = ref<number | null>(null)

  // Step 2 from Repair on: the repairs as they run, then their results
  const isRepairStep = computed(() => isExecuting.value || isFinished.value)
  const selectedRepairRows = computed(() => repairRows.value.filter(isIncluded))
  const selectedCount = computed(() => selectedRepairRows.value.length)
  // Matching: the books left out of the repair. Step 2 only holds the books to repair: while they
  // are repaired, the ones Cancel left out; once over, the ones left out of Organize
  const skippedCount = computed(() => {
    if (isFinished.value) return totalRepairCount.value - audiobookIdsToOrganize.value.length
    if (isExecuting.value) return canceledCount.value
    return repairRows.value.length - selectedCount.value
  })
  const problemText = computed(() => repairProblemText(repairRows.value, canceledCount.value))
  const repairOutcomes = computed(() => toRepairOutcomes(repairRows.value))

  // Each load's number: only the latest one fills the dialog. One still waiting for its answers
  // when the dialog opens again (for other books) or closes fills nothing, whenever they come
  let latestLoadNumber = 0

  async function load() {
    const loadNumber = ++latestLoadNumber
    const isLatestLoad = () => loadNumber === latestLoadNumber
    // The books asked when it opened
    const loadedAudiobookIds = audiobookIds()
    isLoading.value = true
    isFinished.value = false
    isCanceling.value = false
    canceledCount.value = 0
    loadError.value = null
    repairRows.value = []
    searchCache.clear()
    audiobookIdsToOrganize.value = []
    completedRepairCount.value = 0
    totalRepairCount.value = 0
    searchingAudiobookId.value = null
    try {
      const region = await libraryImportStore.configuredRegion()
      if (!isLatestLoad()) return
      configuredRegion.value = region
      // One book at a time, like the repairs (they run in the browser, no backend task yet)
      const loadedRepairRows: RepairRow[] = []
      for (const audiobookId of loadedAudiobookIds) {
        const repairRow = await loadRepairRow(audiobookId, region)
        if (!isLatestLoad()) return
        loadedRepairRows.push(repairRow)
      }
      // None of them: the dialog says why instead of a list of error cards
      const firstLoadError = loadedRepairRows.find((repairRow) => repairRow.loadError)?.loadError
      if (firstLoadError && loadedRepairRows.every((repairRow) => repairRow.loadError)) {
        loadError.value = firstLoadError.message
        return
      }
      repairRows.value = loadedRepairRows
    } catch (e) {
      if (!isLatestLoad()) return
      loadError.value = e instanceof Error ? e.message : 'Failed to load the selected audiobooks'
    } finally {
      if (isLatestLoad()) isLoading.value = false
    }
  }

  // One book and its identifiers. A book that fails gets a card saying why, so the others can
  // still be repaired
  async function loadRepairRow(audiobookId: number, region: string): Promise<RepairRow> {
    let title = ''
    try {
      const book = await apiService.getAudiobook(audiobookId)
      title = book.title ?? ''
      const identifiersResponse = await apiService.getAudiobookIdentifiers(audiobookId)
      return repairRowFrom(audiobookId, book, identifiersResponse.identifiers ?? [], region)
    } catch (e) {
      return failedRepairRow(audiobookId, e, title)
    }
  }

  // Load again, on a book that couldn't be (not a deleted one): its card is replaced by the book,
  // or says why again
  const reloadingAudiobookId = ref<number | null>(null)
  async function loadAgain(audiobookId: number) {
    const index = repairRows.value.findIndex((repairRow) => repairRow.audiobookId === audiobookId)
    if (index < 0 || !repairRows.value[index]!.loadError?.canLoadAgain) return
    const loadNumber = latestLoadNumber
    reloadingAudiobookId.value = audiobookId
    const repairRow = await loadRepairRow(audiobookId, configuredRegion.value)
    reloadingAudiobookId.value = null
    // Loaded afresh meanwhile (Back, opened again): that load has it
    if (loadNumber !== latestLoadNumber) return
    repairRows.value[index] = repairRow
  }

  // The dialog closed: a load still waiting for its answers fills nothing
  function forgetLoad() {
    latestLoadNumber++
    isLoading.value = false
  }

  function onSearching(repairRow: RepairRow, isSearching: boolean) {
    if (isSearching) searchingAudiobookId.value = repairRow.audiobookId
    else if (searchingAudiobookId.value === repairRow.audiobookId) searchingAudiobookId.value = null
  }

  // The cooldown waited, the book's card saying how long is left: false when Cancel ended it
  function waitForRescan(repairRow: RepairRow, seconds: number): Promise<boolean> {
    let remainingSeconds = seconds
    const showRemaining = () => {
      repairRow.repairTask = {
        state: 'running',
        text: `Waiting ${remainingSeconds} s: a book can be rescanned once every ${RESCAN_COOLDOWN_SECONDS} s`,
      }
    }
    showRemaining()
    return new Promise((resolve) => {
      const countdown = setInterval(() => {
        remainingSeconds = Math.max(remainingSeconds - 1, 1)
        showRemaining()
      }, 1000)
      const end = (isWaited: boolean) => {
        clearInterval(countdown)
        clearTimeout(timeout)
        stopRescanWait = null
        resolve(isWaited)
      }
      const timeout = setTimeout(() => end(true), seconds * 1000 + RESCAN_WAIT_MARGIN_MS)
      stopRescanWait = () => end(false)
    })
  }

  async function rescanMetadata(repairRow: RepairRow) {
    try {
      return await apiService.rescanAudiobookMetadata(repairRow.audiobookId)
    } catch (e) {
      const { status, retryAfter } = e as { status?: number; retryAfter?: number }
      if (status !== 429) throw e
      const waitSeconds = retryAfter || RESCAN_COOLDOWN_SECONDS
      if (waitSeconds > MAX_RESCAN_WAIT_SECONDS)
        throw new Error(`too many rescans of this book, retry in about ${waitText(waitSeconds)}`)
      if (!(await waitForRescan(repairRow, waitSeconds))) throw new RescanWaitCanceled()
      repairRow.repairTask = { state: 'running', text: 'Repairing…' }
      return apiService.rescanAudiobookMetadata(repairRow.audiobookId)
    }
  }

  // Step 2 shows the ticked books' repairs: they wait their turn, the others aren't on it.
  // The repairs run one after the other; each card then says how its repair ended and how long it
  // took, and shows the book as it is now. Cancel leaves out the books still waiting. beforeRun
  // is awaited once step 2 is set, before the first repair
  async function repair(beforeRun: () => Promise<void>) {
    const queuedRepairRows = [...selectedRepairRows.value]
    // A pause left from before (the leave question) never holds a new run
    resumeRepair()
    isExecuting.value = true
    isCanceling.value = false
    canceledCount.value = 0
    audiobookIdsToOrganize.value = []
    completedRepairCount.value = 0
    totalRepairCount.value = queuedRepairRows.length
    try {
      for (const repairRow of repairRows.value) {
        repairRow.recordBefore = recordNow(repairRow)
        const pickedSearchResult = queuedRepairRows.includes(repairRow)
          ? repairRow.bookMatch.pickedSearchResult
          : null
        repairRow.plannedRecord = pickedSearchResult
          ? recordFromResult(pickedSearchResult, repairRow.bookMatch.region)
          : null
        repairRow.repairTask = pickedSearchResult
          ? { state: 'waiting', text: 'Waiting to be repaired' }
          : null
      }
      await beforeRun()
      for (const repairRow of queuedRepairRows) {
        await untilResumed()
        // Canceled while it waited
        if (repairRow.repairTask?.state !== 'waiting') continue
        await repairOne(repairRow)
        completedRepairCount.value++
      }
    } catch (e) {
      stopRun(queuedRepairRows, e)
    } finally {
      // Whatever happened, the run ends: the dialog can be closed, nothing guards the page any more
      isExecuting.value = false
      isCanceling.value = false
      isFinished.value = true
    }
  }

  // A book's record as it is now, before its repair
  function recordNow(repairRow: RepairRow): BookRecord {
    return {
      title: repairRow.title,
      subtitle: repairRow.subtitle,
      authors: repairRow.authors,
      asin: repairRow.bookMatch.currentRecordKey.asin,
      region: repairRow.bookMatch.currentRecordKey.region,
    }
  }

  // An error outside a book's own repair stopped the run: the books not repaired yet end failed,
  // saying so, so step 2 shows them and each can be retried; nothing is left "repairing"
  function stopRun(queuedRepairRows: RepairRow[], error: unknown) {
    logger.error('Repair Books: the repairs stopped', error)
    const reason = error instanceof Error ? error.message : 'unknown error'
    for (const repairRow of queuedRepairRows) {
      const state = repairRow.repairTask?.state
      if (state && state !== 'waiting' && state !== 'running') continue
      repairRow.recordBefore ??= recordNow(repairRow)
      repairRow.plannedRecord ??= repairRow.recordBefore
      repairRow.repairTask = { state: 'failed', text: `Repair stopped: ${reason}` }
    }
    completedRepairCount.value = totalRepairCount.value
  }

  // Holds the repairs before the next book; the one being repaired ends (its request can't be
  // taken back)
  function pauseRepair() {
    if (isExecuting.value) isPaused.value = true
  }

  function resumeRepair() {
    isPaused.value = false
    const waiters = resumeWaiters
    resumeWaiters = []
    waiters.forEach((resume) => resume())
  }

  function untilResumed(): Promise<void> {
    if (!isPaused.value) return Promise.resolve()
    return new Promise((resolve) => resumeWaiters.push(resolve))
  }

  // One book's repair: its new record saved (when its ASIN or region changes), its details emptied
  // (it takes its record as a whole, the user agreed to it), then its metadata rescanned and the
  // book read again. Its row says where it is, then how it ended
  async function repairOne(repairRow: RepairRow) {
    repairRow.repairTask = { state: 'running', text: 'Repairing…' }
    const startedAt = performance.now()
    const elapsedText = () => formatDuration(performance.now() - startedAt)
    let isAsinSaved = false
    let isEmptied = false
    try {
      // The new record was looked up in its region: saved first
      if (hasAsinChanged(repairRow.bookMatch)) {
        await apiService.updateAudiobookIdentifiers(
          repairRow.audiobookId,
          identifiersWithPrimaryAsin(repairRow),
        )
        isAsinSaved = true
        hasLibraryChanged.value = true
      }
      // The rescan only writes what the record has: emptied first, the book keeps nothing else
      await apiService.updateAudiobook(repairRow.audiobookId, EMPTIED_RECORD_FIELDS)
      isEmptied = true
      hasLibraryChanged.value = true
      const rescanResult = await rescanMetadata(repairRow)
      hasLibraryChanged.value = true
      const asin = rescanResult.asin ?? repairRow.bookMatch.pickedSearchResult?.asin ?? ''
      const region =
        rescanResult.region ??
        repairRow.bookMatch.pickedSearchResult?.region ??
        repairRow.bookMatch.region
      // The server reads the record from the book's other identifiers when the picked one fails:
      // the book then has another record's metadata, which it says, instead of Repaired
      const pickedAsin = repairRow.bookMatch.pickedSearchResult?.asin ?? ''
      const pickedRegion =
        repairRow.bookMatch.pickedSearchResult?.region || repairRow.bookMatch.region
      const isOtherRecord =
        (!!rescanResult.asin && rescanResult.asin.toUpperCase() !== pickedAsin.toUpperCase()) ||
        (!!rescanResult.region && rescanResult.region !== pickedRegion)
      // The book as saved: in the rescan's answer once it holds it (#1128), else read back. Once
      // the rescan has answered, the book is repaired: a read that fails only leaves the row
      // showing the picked record instead
      let book = (rescanResult as { audiobook?: Audiobook }).audiobook ?? null
      let readBackError: string | null = null
      if (!book)
        try {
          book = await apiService.getAudiobook(repairRow.audiobookId)
        } catch (e) {
          readBackError = e instanceof Error ? e.message : 'unknown error'
        }
      const shownRecord = book ?? (isOtherRecord ? null : repairRow.plannedRecord)
      if (shownRecord) {
        repairRow.title = shownRecord.title || repairRow.title
        // What the server has: none left from the old record
        repairRow.subtitle = shownRecord.subtitle ?? ''
        repairRow.authors = shownRecord.authors ?? []
      }
      repairRow.bookMatch.currentRecordKey = { asin, region }
      repairRow.recordAfter = {
        title: repairRow.title,
        subtitle: repairRow.subtitle,
        authors: repairRow.authors,
        asin,
        region,
      }
      if (isOtherRecord) {
        repairRow.repairTask = {
          state: 'warning',
          text: `The rescan read another record (${bookSummary({ asin, region })}) than the one picked (${elapsedText()}): check the book`,
        }
        return
      }
      // The record it now points at follows, drawn like New (step 2)
      repairRow.repairTask = {
        state: 'done',
        text: readBackError
          ? `Repaired in ${elapsedText()}; the book couldn't be read back to show it: ${readBackError}`
          : `Repaired in ${elapsedText()}`,
      }
      // Ticked for Organize as soon as it is repaired
      audiobookIdsToOrganize.value = [...audiobookIdsToOrganize.value, repairRow.audiobookId]
    } catch (e) {
      const errorMessage = e instanceof Error ? e.message : 'Repair failed'
      // Something saved but its metadata not read: repaired in part, pointing at the picked record
      if (isAsinSaved || isEmptied)
        repairRow.recordAfter = {
          ...repairRow.recordBefore,
          asin: repairRow.bookMatch.pickedSearchResult?.asin ?? '',
          region: repairRow.bookMatch.pickedSearchResult?.region || repairRow.bookMatch.region,
        }
      // What was saved before it stopped
      const savedText = isAsinSaved
        ? isEmptied
          ? 'The new ASIN is saved and its details emptied'
          : 'The new ASIN is saved'
        : 'Its details are emptied'
      if (e instanceof RescanWaitCanceled)
        repairRow.repairTask = {
          state: 'warning',
          text: `${savedText}, but the metadata rescan was canceled`,
        }
      else if (isAsinSaved && !isEmptied)
        repairRow.repairTask = {
          state: 'warning',
          text: `The new ASIN is saved, but its old details couldn't be emptied (${elapsedText()}): ${errorMessage}`,
        }
      else if (isEmptied)
        repairRow.repairTask = {
          state: 'warning',
          text: `${savedText}, but the metadata rescan failed (${elapsedText()}): ${errorMessage}`,
        }
      else
        repairRow.repairTask = {
          state: 'failed',
          text: `Repair failed (${elapsedText()}): ${errorMessage}`,
        }
    }
  }

  // Retry on a book whose repair failed or was done in part, once the repairs are over: that book
  // repaired again, the dialog repairing meanwhile (red Cancel, Organize waiting)
  async function retryRepair(audiobookId: number) {
    const repairRow = repairRows.value.find((candidate) => candidate.audiobookId === audiobookId)
    if (
      !repairRow ||
      isExecuting.value ||
      (repairRow.repairTask?.state !== 'failed' && repairRow.repairTask?.state !== 'warning')
    )
      return
    isExecuting.value = true
    try {
      await repairOne(repairRow)
    } catch (e) {
      stopRun([repairRow], e)
    } finally {
      isExecuting.value = false
      isCanceling.value = false
    }
  }

  // The books still waiting are left out at once; the one being repaired ends first (its request
  // can't be taken back). Cancel also stops it while it waits for the rescan cooldown (its row says
  // so, Retry is there); leaving the page doesn't: its details are already emptied, so it finishes
  // its rescan unseen, as the leave question promises. Paused repairs end too
  function cancelRepair({ isWaitStopped = true } = {}) {
    // Leave chosen after the last repair ended under the question: nothing to cancel, but the pause
    // it set goes, in case the page stays (its navigation failed)
    if (!isExecuting.value) {
      resumeRepair()
      return
    }
    isCanceling.value = true
    if (isWaitStopped) stopRescanWait?.()
    for (const repairRow of repairRows.value)
      if (repairRow.repairTask?.state === 'waiting') {
        repairRow.repairTask = { state: 'skipped', text: 'Canceled: not repaired' }
        canceledCount.value++
        completedRepairCount.value++
      }
    resumeRepair()
  }

  // Every book with a new record
  function selectAll() {
    repairRows.value.forEach((repairRow) => (repairRow.isSelected = isMatched(repairRow)))
  }

  function clearSelection() {
    repairRows.value.forEach((repairRow) => (repairRow.isSelected = false))
  }

  // The repairs run in the browser: once their dialog is gone (its page left), nothing would show
  // them any more, so the books still waiting are left out; the one being repaired finishes
  onScopeDispose(() => cancelRepair({ isWaitStopped: false }))

  return {
    searchCache,
    configuredRegion,
    isLoading,
    loadError,
    repairRows,
    reloadingAudiobookId,
    isExecuting,
    isFinished,
    isCanceling,
    isPaused,
    hasLibraryChanged,
    completedRepairCount,
    totalRepairCount,
    audiobookIdsToOrganize,
    searchingAudiobookId,
    isRepairStep,
    selectedCount,
    skippedCount,
    problemText,
    repairOutcomes,
    load,
    forgetLoad,
    loadAgain,
    onSearching,
    repair,
    retryRepair,
    pauseRepair,
    resumeRepair,
    cancelRepair,
    selectAll,
    clearSelection,
  }
}
