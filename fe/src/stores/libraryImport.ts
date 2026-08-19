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
import type {
  SearchResult,
  AudibleBookMetadata,
  AudiobookSeriesMembership,
  UnmatchedFileItem,
} from '@/types'

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
  // Selection
  selected: boolean
}

const ASIN_PATTERN = /^[A-Z0-9]{10}$/i

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
  // membership; the scalar fields below stay populated from the primary for older consumers.
  const seriesRaw = result.series as unknown
  const seriesEntries = Array.isArray(seriesRaw)
    ? (seriesRaw as Array<{ name?: string; asin?: string; position?: string }>)
    : []
  const seriesMemberships: AudiobookSeriesMembership[] = seriesEntries
    .filter((entry) => (entry?.name ?? '').trim().length > 0)
    .map((entry, index) => {
      const asin = entry.asin?.trim()
      return {
        seriesName: (entry.name ?? '').trim(),
        seriesNumber: entry.position?.trim() || undefined,
        // The search fallback branch fills `asin` with the series *name* when the ASIN
        // re-fetch fails, so only keep a value that actually looks like an ASIN. Until now
        // that bogus value was discarded anyway; a membership would persist it.
        seriesAsin: asin && ASIN_PATTERN.test(asin) ? asin : undefined,
        isPrimary: index === 0,
        sortOrder: index,
      }
    })
  const seriesItem = seriesEntries[0] ?? null
  const series = seriesItem?.name ?? (typeof seriesRaw === 'string' ? seriesRaw : undefined)
  const seriesNumber = seriesItem?.position ?? result.seriesNumber
  const seriesAsin = seriesItem?.asin ?? result.seriesAsin

  return {
    title: result.title ?? '',
    asin: result.asin ?? '',
    authors,
    subtitle: result.subtitle,
    series,
    seriesNumber,
    seriesAsin,
    ...(seriesMemberships.length > 0 ? { seriesMemberships } : {}),
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
  }
}

export const useLibraryImportStore = defineStore('libraryImport', () => {
  const items = ref<Record<string, LibraryImportItem>>({})
  const lookupQueue = ref<string[]>([])
  const isProcessing = ref(false)
  const rootFolderId = ref<number | null>(null)
  const scanStatus = ref<'idle' | 'scanning' | 'done' | 'error'>('idle')
  const scanError = ref<string | null>(null)
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

  async function initFromRootFolder(id: number) {
    rootFolderId.value = id
    scanStatus.value = 'idle'
    try {
      const saved = await apiService.getSavedUnmatchedFiles(id)
      if (saved.lastScannedAt) lastScannedAt.value = saved.lastScannedAt
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
          }
        } else if (p) {
          newItems[item.fullPath] = {
            ...unmatchedToImportItem(item),
            selectedMatch: p.selectedMatch,
            hasSearched: p.hasSearched,
            isSearching: false,
            selected: p.selected,
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
    rootFolderId.value = id
    scanStatus.value = 'scanning'
    scanError.value = null
    try {
      localStorage.removeItem(_storageKey(id))
    } catch {
      /* non-fatal */
    }

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

    async function onComplete(completedJobId: string) {
      if (settled) return
      settled = true
      cleanUp()
      try {
        const response = await apiService.getUnmatchedResults(completedJobId)
        _populateFromItems(response.items)
        _persistMatches()
        lastScannedAt.value = new Date().toISOString()
        scanStatus.value = 'done'
      } catch (e) {
        scanStatus.value = 'error'
        scanError.value = (e as Error)?.message ?? 'Failed to fetch results'
      }
    }

    function onFailed(error?: string) {
      if (settled) return
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
      if (settled) return // SignalR already handled it before this line
      // Poll once immediately — handles fast scans
      const check = await apiService.getUnmatchedResults(jobId)
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

  function _populateFromItems(scanItems: UnmatchedFileItem[]) {
    const newItems: Record<string, LibraryImportItem> = {}
    for (const item of scanItems) {
      const existing = items.value[item.fullPath]
      const fresh = unmatchedToImportItem(item)
      newItems[item.fullPath] = existing
        ? {
            ...fresh,
            selectedMatch: existing.selectedMatch,
            hasSearched: existing.hasSearched,
            selected: existing.selected,
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

  // ─── Queue Processing ─────────────────────────────────────────────────────

  function startProcessing() {
    const unsearched = itemList.value.filter((i) => !i.hasSearched).map((i) => i.id)
    if (unsearched.length === 0) return
    lookupQueue.value = unsearched
    isProcessing.value = true
    metadataFetchCount.value = 0
    processNext()
  }

  function stopProcessing() {
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
    while (isProcessing.value && lookupQueue.value.length > 0) {
      const id: string = lookupQueue.value[0]!
      lookupQueue.value = lookupQueue.value.slice(1)

      const item = items.value[id]
      if (!item) continue

      items.value = { ...items.value, [id]: { ...item, isSearching: true } }

      try {
        const searchParams = buildLibraryImportSearchParams(item, 5)
        const byAsin = !!searchParams.asin
        const results = await apiService.advancedSearch(searchParams)
        metadataFetchCount.value++
        // ASIN results are authoritative — take the first result directly without author comparison
        const first = byAsin ? (results[0] ?? null) : pickBestMatch(results, item.detectedAuthor)
        const current = items.value[id]!
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
    isProcessing.value = false
  }

  // ─── Per-row manual search ────────────────────────────────────────────────

  async function searchItem(id: string, query: string) {
    const item = items.value[id]
    if (!item) return

    items.value[id] = { ...item, isSearching: true }
    try {
      const isAsin = ASIN_PATTERN.test(query.trim())
      const results = await apiService.advancedSearch(
        isAsin ? { asin: query.trim(), cap: 5 } : { title: query, cap: 5 },
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
    items.value[id] = { ...item, selectedMatch: match, hasSearched: true, selected: true }
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
    const base = matchToMetadata(match)
    if (!match.asin) return base
    try {
      type AudiblePayload = {
        authors?: { name?: string }[]
        narrators?: { name?: string }[]
      }
      const resp = await apiService.getAudibleMetadata<
        { source?: string; metadata?: AudiblePayload } | AudiblePayload
      >(match.asin)
      const raw: AudiblePayload =
        resp && 'metadata' in resp && resp.metadata ? resp.metadata : (resp as AudiblePayload)
      const enrichedAuthors = (raw.authors ?? []).map((a) => a?.name ?? '').filter(Boolean)
      const enrichedNarrators = (raw.narrators ?? []).map((n) => n?.name ?? '').filter(Boolean)
      return {
        ...base,
        ...(enrichedAuthors.length > 0 ? { authors: enrichedAuthors } : {}),
        ...(enrichedNarrators.length > 0 ? { narrators: enrichedNarrators } : {}),
      }
    } catch {
      return base
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
    rootFolderId,
    scanStatus,
    scanError,
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
    selectMatch,
    clearMatch,
    toggleSelect,
    toggleSelectAll,
    importSelected,
  }
})
