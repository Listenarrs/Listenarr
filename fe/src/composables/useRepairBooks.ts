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
import { computed, ref } from 'vue'
import { apiService } from '@/services/api'
import { useLibraryImportStore } from '@/stores/libraryImport'
import {
  FALLBACK_REGION,
  asinChanged,
  formatDuration,
  recordFromResult,
  type SearchCache,
} from '@/utils/bookMatch'
import {
  identifiersWithPrimaryAsin,
  isIncluded,
  isMatched,
  repairOutcomes,
  repairProblemText,
  repairRowFrom,
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
  const loading = ref(false)
  const error = ref<string | null>(null)
  const rows = ref<RepairRow[]>([])
  const executing = ref(false)
  const finished = ref(false)
  // Cancel asked while the repairs run: the books still waiting are left out
  const canceling = ref(false)
  // A repair saved something since the dialog opened: closing it, even from step 1 after Back,
  // refreshes the page behind
  const libraryChanged = ref(false)
  // The books Cancel left out, until the dialog loads again or a new repair starts
  const canceledCount = ref(0)
  // The repairs over (whatever their outcome), of how many: the progress bar
  const repairCompleted = ref(0)
  const repairTotal = ref(0)
  // The repaired books to organize: all of them at first
  const organizeIds = ref<number[]>([])
  // One book is searched at a time: the others wait for it
  const searchingRowId = ref<number | null>(null)

  // Step 2 from Repair on: the repairs as they run, then their results
  const repairStep = computed(() => executing.value || finished.value)
  const selectedRows = computed(() => rows.value.filter(isIncluded))
  const selectedCount = computed(() => selectedRows.value.length)
  // Matching: the books left out of the repair. Step 2 only holds the books to repair: while they
  // are repaired, the ones Cancel left out; once over, the ones left out of Organize
  const skippedCount = computed(() => {
    if (finished.value) return repairTotal.value - organizeIds.value.length
    if (executing.value) return canceledCount.value
    return rows.value.length - selectedCount.value
  })
  const problemText = computed(() => repairProblemText(rows.value, canceledCount.value))
  const outcomes = computed(() => repairOutcomes(rows.value))

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
        audiobookIds().map(async (audiobookId) => {
          const [book, ids] = await Promise.all([
            apiService.getAudiobook(audiobookId),
            apiService.getAudiobookIdentifiers(audiobookId),
          ])
          return repairRowFrom(audiobookId, book, ids.identifiers ?? [], configuredRegion.value)
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

  async function rescan(audiobookId: number) {
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
    const toRepair = [...selectedRows.value]
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
    await beforeRun()
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
  async function retryRepair(audiobookId: number) {
    const row = rows.value.find((r) => r.id === audiobookId)
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

  return {
    searchCache,
    configuredRegion,
    loading,
    error,
    rows,
    executing,
    finished,
    canceling,
    libraryChanged,
    repairCompleted,
    repairTotal,
    organizeIds,
    searchingRowId,
    repairStep,
    selectedCount,
    skippedCount,
    problemText,
    outcomes,
    load,
    onSearching,
    repair,
    retryRepair,
    cancelRepair,
    selectAll,
    clearSelection,
  }
}
