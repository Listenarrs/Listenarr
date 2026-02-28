import { defineStore } from 'pinia'
import { ref } from 'vue'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import type { Audiobook } from '@/types'
import { errorTracking } from '@/services/errorTracking'

export const useLibraryStore = defineStore('library', () => {
  const audiobooks = ref<Audiobook[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)
  const selectedIds = ref<Set<number>>(new Set())

  function normalizeLibraryImageUrl(book: Audiobook): Audiobook {
    const current = (book.imageUrl || '').trim()
    const isMissing = current.length === 0
    const isPlaceholder =
      current === '/placeholder.svg' ||
      current === 'placeholder.svg' ||
      current.endsWith('/placeholder.svg') ||
      current.includes('/placeholder.svg?')

    if ((isMissing || isPlaceholder) && book.asin) {
      return {
        ...book,
        imageUrl: `/api/images/${encodeURIComponent(book.asin)}`,
      }
    }

    return book
  }

  async function fetchLibrary() {
    loading.value = true
    error.value = null
    try {
      const serverList = await apiService.getLibrary()
      // Always trust server data - it includes accurate wanted flags based on File.Exists() checks
      audiobooks.value = serverList.map(normalizeLibraryImageUrl)
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to fetch library'
      errorTracking.captureException(err as Error, {
        component: 'LibraryStore',
        operation: 'fetchLibrary',
      })
    } finally {
      loading.value = false
    }
  }

  async function removeFromLibrary(id: number) {
    try {
      await apiService.removeFromLibrary(id)
      // Remove from local state
      audiobooks.value = audiobooks.value.filter((book) => book.id !== id)
      // Remove from selection if selected
      selectedIds.value.delete(id)
      return true
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to remove audiobook'
      errorTracking.captureException(err as Error, {
        component: 'LibraryStore',
        operation: 'removeFromLibrary',
        metadata: { audiobookId: id },
      })
      return false
    }
  }

  async function bulkRemoveFromLibrary(ids: number[]) {
    if (ids.length === 0) return { success: false, deletedCount: 0 }

    try {
      // Backend no longer exposes a single bulk-remove endpoint; perform safe per-id removes
      let deleted = 0
      for (const id of ids) {
        try {
          await apiService.removeFromLibrary(id)
          deleted++
        } catch (e) {
          // Continue attempting remaining deletions even if one fails
        }
      }
      // Remove from local state
      audiobooks.value = audiobooks.value.filter((book) => !ids.includes(book.id))
      // Clear selection
      clearSelection()
      return { success: deleted > 0, deletedCount: deleted }
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to bulk remove audiobooks'
      errorTracking.captureException(err as Error, {
        component: 'LibraryStore',
        operation: 'bulkRemoveFromLibrary',
        metadata: { count: ids.length },
      })
      return { success: false, deletedCount: 0 }
    }
  }

  // Apply a safe, local-only update when the server tells us files were removed for an audiobook
  // Payload shape: { audiobookId: number, removed: Array<{ id: number, path: string }> }
  function applyFilesRemoved(
    payload:
      | { audiobookId: number; removed?: Array<{ id?: number; path?: string }> }
      | null
      | undefined,
  ) {
    if (!payload || typeof payload.audiobookId !== 'number') return

    const bookIndex = audiobooks.value.findIndex((b) => b.id === payload.audiobookId)
    if (bookIndex === -1) return // we don't have this audiobook loaded locally

    const book = audiobooks.value[bookIndex]
    if (!book) return

    const removed = Array.isArray(payload.removed) ? payload.removed : []
    if (removed.length === 0) return

    // Build new files array excluding removed entries (match by id when present, otherwise by path)
    const newFiles = (book.files || []).filter((f) => {
      // If any removed entry matches this file, exclude it
      for (const r of removed) {
        if (typeof r.id === 'number' && typeof f.id === 'number' && r.id === f.id) return false
        if (r.path && f.path && r.path === f.path) return false
      }
      return true
    })

    // Clone the audiobook object and update files safely so reactivity notices the change
    const updated: Audiobook = { ...book, files: newFiles }

    // If the current primary filePath was one of the removed paths, clear it (safe behavior)
    if (book.filePath) {
      const removedPaths = removed.map((r) => r.path).filter(Boolean) as string[]
      if (removedPaths.includes(book.filePath)) {
        updated.filePath = undefined
        updated.fileSize = undefined
      }
    }

    // Replace the item in the array immutably to ensure watchers pick up the change
    audiobooks.value = audiobooks.value.slice()
    audiobooks.value[bookIndex] = updated
  }

  // Register SignalR subscriptions, but skip during unit tests to avoid noisy logs
  // and test-time side effects. Vitest exposes markers on import.meta or globalThis.
  const isVitest = !!(
    (import.meta as unknown as { vitest?: unknown }).vitest ||
    (globalThis as unknown as { __vitest?: unknown }).__vitest
  )
  if (!isVitest) {
    try {
      // Register a SignalR subscription once when the store is created so we can keep local state in sync
      // We intentionally do not unsubscribe because the store's lifetime matches the app lifetime.
      signalRService.onFilesRemoved((payload) => {
        try {
          applyFilesRemoved(
            payload as { audiobookId: number; removed?: Array<{ id?: number; path?: string }> },
          )
        } catch (e) {
          // Defensive: don't allow signal handler errors to break the app
          errorTracking.captureException(e as Error, {
            component: 'LibraryStore',
            operation: 'signalr.onFilesRemoved',
          })
        }
      })

      signalRService.onAudiobookUpdate((updatedAudiobook) => {
        try {
          const index = audiobooks.value.findIndex((b) => b.id === updatedAudiobook.id)
          if (index !== -1) {
            // Update the audiobook in the store, preserving reactivity
            audiobooks.value = audiobooks.value.slice()
            const prev = audiobooks.value[index]
            if (!prev) return
            const merged = { ...prev, ...updatedAudiobook }
            // Preserve basePath if server payload omits or clears it
            if (
              (!('basePath' in updatedAudiobook) || !updatedAudiobook.basePath) &&
              prev.basePath
            ) {
              merged.basePath = prev.basePath
            }
            audiobooks.value[index] = normalizeLibraryImageUrl(merged)
          }
        } catch (e) {
          // Defensive: don't allow signal handler errors to break the app
          errorTracking.captureException(e as Error, {
            component: 'LibraryStore',
            operation: 'signalr.onAudiobookUpdate',
          })
        }
      })
    } catch {
      // If signalRService isn't ready at module import time, this will be a no-op; we'll still sync on next fetchLibrary
    }
  }

  function toggleSelection(id: number) {
    if (selectedIds.value.has(id)) {
      selectedIds.value.delete(id)
    } else {
      selectedIds.value.add(id)
    }
  }

  function selectAll() {
    audiobooks.value.forEach((book) => selectedIds.value.add(book.id))
  }

  function clearSelection() {
    selectedIds.value.clear()
  }

  function isSelected(id: number): boolean {
    return selectedIds.value.has(id)
  }

  return {
    audiobooks,
    loading,
    error,
    selectedIds,
    fetchLibrary,
    removeFromLibrary,
    bulkRemoveFromLibrary,
    toggleSelection,
    selectAll,
    clearSelection,
    isSelected,
  }
})
