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
import { useLibraryImportStore } from '@/stores/libraryImport'
import {
  FALLBACK_REGION,
  hasAsinChanged,
  formatDuration,
  recordFromResult,
  type SearchCache,
} from '@/utils/bookMatch'
import {
  identifiersWithPrimaryAsin,
  isIncluded,
  isMatched,
  repairProblemText,
  repairRowFrom,
  toRepairOutcomes,
  type RepairRow,
} from '@/utils/repair'

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

  async function load() {
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
      configuredRegion.value = await libraryImportStore.configuredRegion()
      repairRows.value = await Promise.all(
        audiobookIds().map(async (audiobookId) => {
          const [book, identifiersResponse] = await Promise.all([
            apiService.getAudiobook(audiobookId),
            apiService.getAudiobookIdentifiers(audiobookId),
          ])
          return repairRowFrom(
            audiobookId,
            book,
            identifiersResponse.identifiers ?? [],
            configuredRegion.value,
          )
        }),
      )
    } catch (e) {
      loadError.value = e instanceof Error ? e.message : 'Failed to load the selected audiobooks'
    } finally {
      isLoading.value = false
    }
  }

  function onSearching(repairRow: RepairRow, isSearching: boolean) {
    if (isSearching) searchingAudiobookId.value = repairRow.audiobookId
    else if (searchingAudiobookId.value === repairRow.audiobookId) searchingAudiobookId.value = null
  }

  async function rescanMetadata(audiobookId: number) {
    try {
      return await apiService.rescanAudiobookMetadata(audiobookId)
    } catch (e) {
      // A rescan right after another one for the same book is refused (429): wait and retry once
      if ((e as { status?: number })?.status !== 429) throw e
      await new Promise((resolve) => setTimeout(resolve, 3000))
      return apiService.rescanAudiobookMetadata(audiobookId)
    }
  }

  // Step 2 shows the ticked books' repairs: they wait their turn, the others aren't on it.
  // The repairs run one after the other; each card then says how its repair ended and how long it
  // took, and shows the book as it is now. Cancel leaves out the books still waiting. beforeRun
  // is awaited once step 2 is set, before the first repair
  async function repair(beforeRun: () => Promise<void>) {
    const queuedRepairRows = [...selectedRepairRows.value]
    isExecuting.value = true
    isCanceling.value = false
    canceledCount.value = 0
    audiobookIdsToOrganize.value = []
    completedRepairCount.value = 0
    totalRepairCount.value = queuedRepairRows.length
    for (const repairRow of repairRows.value) {
      repairRow.recordBefore = {
        title: repairRow.title,
        subtitle: repairRow.subtitle,
        authors: repairRow.authors,
        asin: repairRow.bookMatch.currentRecordKey.asin,
        region: repairRow.bookMatch.currentRecordKey.region,
      }
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
    isExecuting.value = false
    isCanceling.value = false
    isFinished.value = true
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

  // One book's repair: its new record saved (when its ASIN or region changes), then its metadata
  // rescanned and the book read again. Its row says where it is, then how it ended
  async function repairOne(repairRow: RepairRow) {
    repairRow.repairTask = { state: 'running', text: 'Repairing…' }
    const startedAt = performance.now()
    const elapsedText = () => formatDuration(performance.now() - startedAt)
    let isAsinSaved = false
    try {
      // The new record was looked up in its region: saved, then the metadata read from it
      if (hasAsinChanged(repairRow.bookMatch)) {
        await apiService.updateAudiobookIdentifiers(
          repairRow.audiobookId,
          identifiersWithPrimaryAsin(repairRow),
        )
        isAsinSaved = true
        hasLibraryChanged.value = true
      }
      const rescanResult = await rescanMetadata(repairRow.audiobookId)
      const book = await apiService.getAudiobook(repairRow.audiobookId)
      const asin = rescanResult.asin ?? repairRow.bookMatch.pickedSearchResult?.asin ?? ''
      const region =
        rescanResult.region ??
        repairRow.bookMatch.pickedSearchResult?.region ??
        repairRow.bookMatch.region
      repairRow.title = book.title || repairRow.title
      repairRow.subtitle = book.subtitle ?? repairRow.subtitle
      repairRow.authors = book.authors ?? repairRow.authors
      repairRow.bookMatch.currentRecordKey = { asin, region }
      repairRow.recordAfter = {
        title: repairRow.title,
        subtitle: repairRow.subtitle,
        authors: repairRow.authors,
        asin,
        region,
      }
      // The record it now points at follows, drawn like New (step 2)
      repairRow.repairTask = { state: 'done', text: `Repaired in ${elapsedText()}` }
      hasLibraryChanged.value = true
      // Ticked for Organize as soon as it is repaired
      audiobookIdsToOrganize.value = [...audiobookIdsToOrganize.value, repairRow.audiobookId]
    } catch (e) {
      const errorMessage = e instanceof Error ? e.message : 'Repair failed'
      // The new ASIN saved but its metadata not read: repaired in part
      if (isAsinSaved)
        repairRow.recordAfter = {
          ...repairRow.recordBefore,
          asin: repairRow.bookMatch.pickedSearchResult?.asin ?? '',
          region: repairRow.bookMatch.pickedSearchResult?.region || repairRow.bookMatch.region,
        }
      repairRow.repairTask = isAsinSaved
        ? {
            state: 'warning',
            text: `The new ASIN is saved, but the metadata rescan failed (${elapsedText()}): ${errorMessage}`,
          }
        : { state: 'failed', text: `Repair failed (${elapsedText()}): ${errorMessage}` }
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
    await repairOne(repairRow)
    isExecuting.value = false
    isCanceling.value = false
  }

  // The books still waiting are left out at once; the one being repaired ends first (its request
  // can't be taken back). Paused repairs end too
  function cancelRepair() {
    if (!isExecuting.value) return
    isCanceling.value = true
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
  // them any more, so the books still waiting are left out, as by Cancel
  onScopeDispose(cancelRepair)

  return {
    searchCache,
    configuredRegion,
    isLoading,
    loadError,
    repairRows,
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
