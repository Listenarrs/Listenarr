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
import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { logger } from '@/utils/logger'
import { buildLibraryImportSearchParams } from '@/utils/libraryImportSearch'
import { normalizeSearchRegion, searchRegionOptions } from '@/utils/languageMapping'
import { useConfigurationStore } from '@/stores/configuration'
import { buildSeriesFields, looksLikeAsin } from '@/utils/seriesUtils'
import type { SeriesEntry } from '@/utils/seriesUtils'
import type { SearchResult, AudibleBookMetadata, UnmatchedFileItem } from '@/types'

export interface LibraryImportItem {
  id: string // = fullPath (unique key)
  fullPath: string // path to audio file
  sourceFiles: string[] // all source files represented by this row
  folderPath: string // bookFolder (parent directory)
  relativePath: string // relative to root folder
  folderName: string // search term: last non-empty path segment
  // Detected from file scan
  detectedTitle?: string
  detectedAuthor?: string
  detectedAsin?: string
  detectedSeries?: string
  format: string
  fileCount: number
  // Match state
  selectedMatch: SearchResult | null
  hasSearched: boolean // true once auto-search was attempted
  isSearching: boolean // currently in-flight
  // Why the last import dropped the match (its Audible record could not be found)
  matchError?: string
  // Selection
  selected: boolean
}

function extractFolderName(relativePath: string): string {
  const parts = relativePath.replace(/\\/g, '/').split('/').filter(Boolean)
  // Prefer the last meaningful segment (author/title structure)
  return parts[parts.length - 1] ?? relativePath
}

// From a list of search results, prefer the one whose author best matches detectedAuthor.
// Falls back to results[0] when no author info is available on either side.
function pickBestMatch(results: SearchResult[], detectedAuthor?: string): SearchResult | null {
  if (!results.length) return null
  if (!detectedAuthor) return results[0] ?? null
  const needle = detectedAuthor.toLowerCase()
  const scored = results.map((r) => {
    const resultAuthor = (r.authors?.[0]?.name ?? '').toLowerCase()
    const match = resultAuthor && (resultAuthor.includes(needle) || needle.includes(resultAuthor))
    return { r, match }
  })
  return scored.find((s) => s.match)?.r ?? results[0] ?? null
}

// The Audible catalogue has no usable record for the match's ASIN in its region: the book must not
// be imported without it, the user picks the match (and its region) again
class MatchRecordNotFoundError extends Error {}

function regionLabel(region: string): string {
  return searchRegionOptions.find((o) => o.value === region)?.label ?? region
}

type AdvancedSearchParams = Parameters<typeof apiService.advancedSearch>[0]

// Keep usable results only (Audible answers an ASIN it doesn't sell in that market with an empty
// stub: an ASIN but no title) and tag each one with the region it came from.
function usableInRegion(results: SearchResult[], region: string): SearchResult[] {
  return results
    .filter((result) => !(result.asin && !result.title))
    .map((result) => ({ ...result, region: result.region || region }))
}

function normalizeGenres(genres: unknown): string[] | undefined {
  if (!Array.isArray(genres)) return genres as string[] | undefined
  return genres
    .map((g) => (typeof g === 'string' ? g : ((g as { name?: string })?.name ?? '')))
    .filter(Boolean)
}

function unmatchedToImportItem(item: UnmatchedFileItem): LibraryImportItem {
  return {
    id: item.fullPath,
    fullPath: item.fullPath,
    sourceFiles:
      item.sourceFiles && item.sourceFiles.length > 0 ? item.sourceFiles : [item.fullPath],
    folderPath: item.bookFolder,
    relativePath: item.relativePath,
    folderName: extractFolderName(item.relativePath),
    detectedTitle: item.title,
    detectedAuthor: item.author,
    detectedAsin: item.asin,
    detectedSeries: item.series,
    format: item.format,
    fileCount: item.fileCount,
    selectedMatch: null,
    hasSearched: false,
    isSearching: false,
    selected: false,
  }
}

function matchToMetadata(result: SearchResult): AudibleBookMetadata {
  const authors: string[] =
    result.authors && result.authors.length > 0
      ? result.authors.map((a) => a.name ?? '').filter(Boolean)
      : []

  // series may come back as AudibleSeries[] from the search endpoint. A book can belong to
  // more than one series (Audnexus seriesPrimary/seriesSecondary), so every entry becomes a
  // membership; the scalar fields stay populated from the primary for older consumers.
  const seriesRaw = result.series as unknown
  const seriesEntries = Array.isArray(seriesRaw) ? (seriesRaw as SeriesEntry[]) : []
  const seriesFields = buildSeriesFields(seriesEntries)
  const series = seriesFields.series ?? (typeof seriesRaw === 'string' ? seriesRaw : undefined)
  const seriesNumber = seriesFields.seriesNumber ?? result.seriesNumber
  const seriesAsin = seriesFields.seriesAsin ?? result.seriesAsin

  return {
    title: result.title ?? '',
    asin: result.asin ?? '',
    authors,
    subtitle: result.subtitle,
    series,
    seriesNumber,
    seriesAsin,
    ...(seriesFields.seriesMemberships
      ? { seriesMemberships: seriesFields.seriesMemberships }
      : {}),
    description: result.description,
    publisher: result.publisher,
    language: result.language,
    runtime: result.runtime ?? (result.lengthMinutes ? result.lengthMinutes * 60 : undefined),
    imageUrl: result.imageUrl,
    // SearchResult.genres comes as objects {asin, name, type} from Audible;
    // AudibleBookMetadata.genres expects string[] (genre names only)
    genres: normalizeGenres(result.genres),
    narrators: result.narrators?.map((n) => n.name ?? '').filter(Boolean),
    publishYear: result.releaseDate?.substring(0, 4) ?? result.publishDate?.substring(0, 4),
    metadataSource: result.metadataSource,
    region: result.region,
  }
}

export const useLibraryImportStore = defineStore('libraryImport', () => {
  const items = ref<Record<string, LibraryImportItem>>({})
  const lookupQueue = ref<string[]>([])
  const isProcessing = ref(false)
  // Find Match searches running in their modals: importing waits until none is left, so a row can't
  // be imported while its match is being chosen
  const findMatchSearchCount = ref(0)
  const findMatchSearching = computed(() => findMatchSearchCount.value > 0)
  function findMatchSearchStarted() {
    findMatchSearchCount.value += 1
  }
  function findMatchSearchEnded() {
    findMatchSearchCount.value = Math.max(0, findMatchSearchCount.value - 1)
  }
  const rootFolderId = ref<number | null>(null)
  const scanStatus = ref<'idle' | 'scanning' | 'done' | 'error'>('idle')
  const scanError = ref<string | null>(null)
  const scanWarnings = ref<string[]>([])
  const lastScannedAt = ref<string | null>(null)
  const action = ref<'none' | 'move' | 'hardlink/copy'>('none')
  const monitor = ref<'none' | 'all'>('all')
  const metadataFetchCount = ref(0)
  const importErrors = ref<string[]>([])

  // ─── Computed ────────────────────────────────────────────────────────────────

  const itemList = computed(() => Object.values(items.value))
  const selectedCount = computed(() => itemList.value.filter((i) => i.selected).length)
  const hasUnprocessedItems = computed(() => itemList.value.some((i) => !i.hasSearched))
  const processedCount = computed(() => itemList.value.filter((i) => i.hasSearched).length)
  const matchedCount = computed(() => itemList.value.filter((i) => i.selectedMatch).length)

  // ─── Scan ─────────────────────────────────────────────────────────────────

  // Only the latest load or scan may write the state: starting one stops listening to the previous
  // scan, and an older one answering late is ignored
  let currentOperation = 0
  let stopCurrentScan: (() => void) | null = null

  function _beginOperation(folderId: number): number {
    stopCurrentScan?.()
    stopCurrentScan = null
    // Start Matching works on the rows shown: another folder's rows replace them, so it stops
    if (folderId !== rootFolderId.value) stopProcessing()
    return ++currentOperation
  }

  async function initFromRootFolder(id: number) {
    const operation = _beginOperation(id)
    rootFolderId.value = id
    scanStatus.value = 'idle'
    scanWarnings.value = []
    try {
      const saved = await apiService.getSavedUnmatchedFiles(id)
      if (operation !== currentOperation) return
      if (saved.lastScannedAt) lastScannedAt.value = saved.lastScannedAt
      scanWarnings.value = saved.warnings ?? []
      const persisted = _loadPersistedMatches(id)
      const newItems: Record<string, LibraryImportItem> = {}
      for (const item of saved.items) {
        const existing = items.value[item.fullPath]
        const p = persisted[item.fullPath]
        if (existing) {
          newItems[item.fullPath] = {
            ...unmatchedToImportItem(item),
            selectedMatch: existing.selectedMatch,
            hasSearched: existing.hasSearched,
            isSearching: false,
            selected: existing.selected,
            matchError: existing.matchError,
          }
        } else if (p) {
          newItems[item.fullPath] = {
            ...unmatchedToImportItem(item),
            selectedMatch: p.selectedMatch,
            hasSearched: p.hasSearched,
            isSearching: false,
            selected: p.selected,
            matchError: p.matchError,
          }
        } else {
          newItems[item.fullPath] = unmatchedToImportItem(item)
        }
      }
      items.value = newItems
      if (saved.items.length > 0) scanStatus.value = 'done'
    } catch (e) {
      logger.debug('[libraryImport] Failed to load saved results:', e)
    }
  }

  async function triggerScan(id: number) {
    const operation = _beginOperation(id)
    const isCurrent = () => operation === currentOperation
    // Matches already made in this folder carry over to the new results, even when it wasn't loaded
    const persisted = _loadPersistedMatches(id)
    if (rootFolderId.value !== id) {
      // Another folder's rows must not show under this one, even if the scan fails
      items.value = {}
      lastScannedAt.value = null
    }
    rootFolderId.value = id
    scanStatus.value = 'scanning'
    scanError.value = null
    scanWarnings.value = []
    // The saved matches stay until this scan's results replace them: a scan that replaces this one
    // reads them again

    let jobId = ''
    let settled = false
    let offSignalR: (() => void) | null = null
    let pollInterval: ReturnType<typeof setInterval> | null = null

    function cleanUp() {
      offSignalR?.()
      if (pollInterval) {
        clearInterval(pollInterval)
        pollInterval = null
      }
    }

    stopCurrentScan = cleanUp

    async function onComplete(completedJobId: string) {
      if (settled || !isCurrent()) return
      settled = true
      cleanUp()
      try {
        const response = await apiService.getUnmatchedResults(completedJobId)
        if (!isCurrent()) return
        _populateFromItems(response.items, persisted)
        scanWarnings.value = response.warnings ?? []
        _persistMatches()
        lastScannedAt.value = new Date().toISOString()
        scanStatus.value = 'done'
      } catch (e) {
        if (!isCurrent()) return
        scanStatus.value = 'error'
        scanError.value = (e as Error)?.message ?? 'Failed to fetch results'
      }
    }

    function onFailed(error?: string) {
      if (settled || !isCurrent()) return
      settled = true
      cleanUp()
      scanStatus.value = 'error'
      scanError.value = error ?? 'Scan failed'
    }

    // Register before POST — if scan is fast, SignalR may fire before scanUnmatchedFiles() returns.
    // Allow the event if jobId is not yet assigned (jobId === '') — it must be ours.
    offSignalR = signalRService.onUnmatchedScanComplete(async (payload) => {
      if (!jobId || payload.jobId !== jobId) return
      if (payload.error) {
        onFailed(payload.error)
        return
      }
      await onComplete(payload.jobId)
    })

    try {
      const result = await apiService.scanUnmatchedFiles(id)
      jobId = result.jobId
      if (settled || !isCurrent()) return // SignalR already handled it, or another scan replaced it
      // Poll once immediately — handles fast scans
      const check = await apiService.getUnmatchedResults(jobId)
      if (!isCurrent()) return
      if (check.status === 'Completed') {
        await onComplete(jobId)
      } else if (check.status === 'Failed') {
        onFailed(check.error)
      } else {
        // Polling fallback — keeps checking every 2.5s if SignalR is unavailable or slow
        pollInterval = setInterval(async () => {
          if (settled || !jobId) return
          try {
            const poll = await apiService.getUnmatchedResults(jobId)
            if (poll.status === 'Completed') await onComplete(jobId)
            else if (poll.status === 'Failed') onFailed(poll.error)
          } catch {
            /* ignore transient errors, keep polling */
          }
        }, 2500)
      }
    } catch (e) {
      onFailed((e as Error)?.message ?? 'Failed to start scan')
    }
  }

  function _populateFromItems(
    scanItems: UnmatchedFileItem[],
    persisted: Record<string, PersistedEntry> = {},
  ) {
    const newItems: Record<string, LibraryImportItem> = {}
    for (const item of scanItems) {
      const existing = items.value[item.fullPath] ?? persisted[item.fullPath]
      const fresh = unmatchedToImportItem(item)
      newItems[item.fullPath] = existing
        ? {
            ...fresh,
            selectedMatch: existing.selectedMatch,
            hasSearched: existing.hasSearched,
            selected: existing.selected,
            matchError: existing.matchError,
          }
        : fresh
    }
    items.value = newItems
  }

  // ─── localStorage persistence ──────────────────────────────────────────────

  type PersistedEntry = {
    selectedMatch: SearchResult | null
    hasSearched: boolean
    selected: boolean
    matchError?: string
  }

  function _storageKey(folderId: number): string {
    return `listenarr-import-matches-${folderId}`
  }

  function _persistMatches(): void {
    if (!rootFolderId.value) return
    const state: Record<string, PersistedEntry> = {}
    for (const [path, item] of Object.entries(items.value)) {
      state[path] = {
        selectedMatch: item.selectedMatch,
        hasSearched: item.hasSearched,
        selected: item.selected,
        matchError: item.matchError,
      }
    }
    try {
      localStorage.setItem(_storageKey(rootFolderId.value), JSON.stringify(state))
    } catch {
      /* non-fatal */
    }
  }

  function _loadPersistedMatches(folderId: number): Record<string, PersistedEntry> {
    try {
      const raw = localStorage.getItem(_storageKey(folderId))
      if (!raw) return {}
      return JSON.parse(raw) as Record<string, PersistedEntry>
    } catch {
      return {}
    }
  }

  // ─── Region-aware search ──────────────────────────────────────────────────

  async function _configuredRegion(): Promise<string> {
    const configStore = useConfigurationStore()
    if (!configStore.applicationSettings) await configStore.loadApplicationSettings()
    return normalizeSearchRegion(configStore.applicationSettings?.defaultSearchRegion)
  }

  // Search one Audible region: the one given (picked by the user) or the configured one. There is no
  // automatic switch to another catalogue. Results are tagged with their region, so the import
  // fetches their full metadata (authors, narrators) from the same catalogue and keeps that region.
  async function searchInRegion(
    params: AdvancedSearchParams,
    region?: string,
    signal?: AbortSignal,
  ): Promise<SearchResult[]> {
    const searchRegion = region || (await _configuredRegion())
    const results = signal
      ? await apiService.advancedSearch({ ...params, region: searchRegion }, signal)
      : await apiService.advancedSearch({ ...params, region: searchRegion })
    return usableInRegion(results, searchRegion)
  }

  // ─── Queue Processing ─────────────────────────────────────────────────────

  // Only the latest Start Matching run may go on: a stopped one ends after its search in flight
  let currentMatchingRun = 0

  function startProcessing() {
    const unsearched = itemList.value.filter((i) => !i.hasSearched).map((i) => i.id)
    if (unsearched.length === 0) return
    lookupQueue.value = unsearched
    isProcessing.value = true
    metadataFetchCount.value = 0
    processNext()
  }

  function stopProcessing() {
    currentMatchingRun++
    lookupQueue.value = []
    isProcessing.value = false
    // Clear any in-flight isSearching flags
    for (const id of Object.keys(items.value)) {
      const entry = items.value[id]
      if (entry?.isSearching) {
        items.value = { ...items.value, [id]: { ...entry, isSearching: false } }
      }
    }
  }

  async function processNext() {
    const run = ++currentMatchingRun
    while (run === currentMatchingRun && isProcessing.value && lookupQueue.value.length > 0) {
      const id: string = lookupQueue.value[0]!
      lookupQueue.value = lookupQueue.value.slice(1)

      const item = items.value[id]
      if (!item) continue

      items.value = { ...items.value, [id]: { ...item, isSearching: true } }

      try {
        const searchParams = buildLibraryImportSearchParams(item, 5)
        const byAsin = !!searchParams.asin
        const results = await searchInRegion(searchParams)
        metadataFetchCount.value++
        // ASIN results are authoritative — take the first result directly without author comparison
        const first = byAsin ? (results[0] ?? null) : pickBestMatch(results, item.detectedAuthor)
        // The row may have left the page meanwhile (another folder loaded or scanned)
        const current = items.value[id]
        if (!current) continue
        items.value = {
          ...items.value,
          [id]: {
            ...current,
            isSearching: false,
            hasSearched: true,
            selectedMatch: first,
            selected: first !== null,
          },
        }
        _persistMatches()
      } catch {
        const current = items.value[id]
        if (current)
          items.value = {
            ...items.value,
            [id]: { ...current, isSearching: false, hasSearched: true },
          }
      }
    }
    if (run === currentMatchingRun) isProcessing.value = false
  }

  // ─── Per-row manual search ────────────────────────────────────────────────

  async function searchItem(id: string, query: string, region?: string) {
    const item = items.value[id]
    if (!item) return

    items.value[id] = { ...item, isSearching: true }
    try {
      const isAsin = looksLikeAsin(query)
      const results = await searchInRegion(
        isAsin ? { asin: query.trim(), cap: 5 } : { title: query, cap: 5 },
        region,
      )
      items.value[id] = { ...items.value[id], isSearching: false, hasSearched: true }
      return results
    } catch {
      items.value[id] = { ...items.value[id], isSearching: false }
      return []
    }
  }

  // ─── Match management ─────────────────────────────────────────────────────

  function selectMatch(id: string, match: SearchResult) {
    const item = items.value[id]
    if (!item) return
    items.value[id] = {
      ...item,
      selectedMatch: match,
      hasSearched: true,
      selected: true,
      matchError: undefined,
    }
    _persistMatches()
  }

  function clearMatch(id: string) {
    const item = items.value[id]
    if (!item) return
    items.value[id] = { ...item, selectedMatch: null, selected: false }
    _persistMatches()
  }

  // ─── Selection ────────────────────────────────────────────────────────────

  function toggleSelect(id: string) {
    const item = items.value[id]
    if (!item) return
    items.value[id] = { ...item, selected: !item.selected }
    _persistMatches()
  }

  function toggleSelectAll() {
    const allSelected = itemList.value.filter((i) => i.selectedMatch).every((i) => i.selected)
    for (const item of itemList.value) {
      if (item.selectedMatch) {
        items.value[item.id] = { ...item, selected: !allSelected }
      }
    }
    _persistMatches()
  }

  // ─── Import ───────────────────────────────────────────────────────────────

  // Enrich metadata with full Audible data before adding to library.
  // Search results often have authors: [{ asin, name: undefined }] — the full
  // metadata fetch is the only way to get real author/narrator names.
  async function _enrichMetadata(match: SearchResult): Promise<AudibleBookMetadata> {
    // A match saved before regions were recorded has none: use (and keep) the configured region
    const region = match.region || (await _configuredRegion())
    const base = { ...matchToMetadata(match), region }
    if (!match.asin) return base
    const notFound = new MatchRecordNotFoundError(
      `Audible ${regionLabel(region)} has no record for ${match.asin}. ` +
        'Use "Search for a match" to pick the book again, in the right region.',
    )
    type AudiblePayload = {
      asin?: string
      title?: string
      authors?: { name?: string }[]
      narrators?: { name?: string }[]
      series?: SeriesEntry[]
    }
    let resp: { source?: string; metadata?: AudiblePayload } | AudiblePayload
    try {
      resp = await apiService.getAudibleMetadata<
        { source?: string; metadata?: AudiblePayload } | AudiblePayload
      >(match.asin, region)
    } catch (e: unknown) {
      if ((e as { status?: number })?.status === 404) throw notFound
      throw new Error(`Audible metadata lookup failed: ${(e as Error)?.message ?? 'unknown error'}`)
    }
    const raw: AudiblePayload =
      (resp && 'metadata' in resp && resp.metadata ? resp.metadata : (resp as AudiblePayload)) ?? {}
    // Audible answers an ASIN it doesn't sell in that market with an empty stub: ASIN, no title
    if (raw.asin && !raw.title) throw notFound
    const enrichedAuthors = (raw.authors ?? []).map((a) => a?.name ?? '').filter(Boolean)
    const enrichedNarrators = (raw.narrators ?? []).map((n) => n?.name ?? '').filter(Boolean)
    // The product record is the better source of series data than the search match, and
    // its memberships and legacy scalars have to move together.
    const enrichedSeries = buildSeriesFields(raw.series)
    return {
      ...base,
      ...(enrichedAuthors.length > 0 ? { authors: enrichedAuthors } : {}),
      ...(enrichedNarrators.length > 0 ? { narrators: enrichedNarrators } : {}),
      ...(enrichedSeries.seriesMemberships ? enrichedSeries : {}),
    }
  }

  async function importSelected(
    rootFolderPath: string,
  ): Promise<{ imported: number; errors: string[]; warnings: string[] }> {
    const toImport = itemList.value.filter((i) => i.selected && i.selectedMatch)
    importErrors.value = []
    const warnings: string[] = []
    let imported = 0

    for (const item of toImport) {
      const match = item.selectedMatch!
      try {
        let audiobookId: number
        try {
          const metadata = await _enrichMetadata(match)
          const sanitizedMatch = {
            ...match,
            genres: normalizeGenres(match.genres),
            series: Array.isArray(match.series)
              ? ((match.series as Array<{ name?: string }>)[0]?.name ?? undefined)
              : match.series,
          }
          const { audiobook } = await apiService.addToLibrary(metadata, {
            monitored: monitor.value != 'none',
            destinationPath: action.value === 'none' ? item.folderPath : rootFolderPath,
            searchResult: sanitizedMatch,
          })
          audiobookId = audiobook.id
        } catch (e: unknown) {
          // 409 = book already in library, extract existing audiobook from response body
          const err = e as { status?: number; body?: unknown }
          if (err?.status === 409 && err?.body) {
            const body = typeof err.body === 'string' ? JSON.parse(err.body) : err.body
            if (body?.audiobook?.id) {
              audiobookId = body.audiobook.id
              // Mutation imports may compatibility-route an existing audiobook to the
              // selected destination. In-place registration must never rewrite BasePath:
              // the existing file has to belong to the audiobook's current managed folder.
              if (action.value !== 'none' && rootFolderPath) {
                try {
                  await apiService.updateAudiobook(audiobookId, { basePath: rootFolderPath })
                } catch {
                  // Non-critical — import continues, file may go to OutputPath fallback
                }
              }
            } else {
              throw e
            }
          } else {
            throw e
          }
        }
        const importResult = await apiService.startManualImport({
          path: item.folderPath,
          mode: 'interactive',
          action: action.value,
          includeCompanionFiles: action.value !== 'none',
          cleanupEmptySourceFolders: action.value === 'move',
          items: item.sourceFiles.map((fullPath) => ({
            fullPath,
            matchedAudiobookId: audiobookId,
          })),
        })
        const failedResult = importResult.results?.find((result) => !result.success)
        if (failedResult || importResult.importedCount !== item.sourceFiles.length) {
          const reason = failedResult?.error ?? failedResult?.skipReason
          throw new Error(
            reason ??
              `Only ${importResult.importedCount} of ${item.sourceFiles.length} file(s) were imported`,
          )
        }

        for (const result of importResult.results ?? []) {
          if (result.success && result.warning && !warnings.includes(result.warning)) {
            warnings.push(result.warning)
          }
        }

        // Remove imported item from store only after the backend confirms every
        // source file represented by this book row was registered successfully.
        const updated = { ...items.value }
        delete updated[item.id]
        items.value = updated
        _persistMatches()
        imported++
      } catch (e) {
        if (e instanceof MatchRecordNotFoundError) {
          // Leave the row unresolved, with the reason, rather than import a book with no metadata.
          // Only if it still holds the match that failed: a match chosen meanwhile stays.
          const current = items.value[item.id]
          if (current && current.selectedMatch === match) {
            items.value = {
              ...items.value,
              [item.id]: {
                ...current,
                selectedMatch: null,
                hasSearched: true,
                selected: false,
                matchError: e.message,
              },
            }
            _persistMatches()
          }
        }
        const msg = `${item.folderName}: ${(e as Error)?.message ?? 'Import failed'}`
        importErrors.value.push(msg)
        logger.debug('[libraryImport] Import error:', msg)
      }
    }

    return { imported, errors: importErrors.value, warnings }
  }

  return {
    // State
    items,
    lookupQueue,
    isProcessing,
    findMatchSearching,
    findMatchSearchStarted,
    findMatchSearchEnded,
    rootFolderId,
    scanStatus,
    scanError,
    scanWarnings,
    lastScannedAt,
    action,
    monitor,
    metadataFetchCount,
    importErrors,
    // Computed
    itemList,
    selectedCount,
    hasUnprocessedItems,
    processedCount,
    matchedCount,
    // Actions
    initFromRootFolder,
    triggerScan,
    startProcessing,
    stopProcessing,
    processNext,
    searchItem,
    searchInRegion,
    configuredRegion: _configuredRegion,
    selectMatch,
    clearMatch,
    toggleSelect,
    toggleSelectAll,
    importSelected,
  }
})
