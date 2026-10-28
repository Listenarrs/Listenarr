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
import { ref } from 'vue'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import type { Audiobook } from '@/types'
import { errorTracking } from '@/services/errorTracking'
import { buildApiPath } from '@/services/apiBase'
import { useLibraryDeleteOperationsStore } from '@/stores/libraryDeleteOperations'

export const useLibraryStore = defineStore('library', () => {
  const audiobooks = ref<Audiobook[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)
  const selectedIds = ref<Set<number>>(new Set())
  let inFlightFetch: Promise<void> | null = null
  const deleteOperationsStore = useLibraryDeleteOperationsStore()

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
        imageUrl: buildApiPath(`/images/${encodeURIComponent(book.asin)}`),
      }
    }

    return book
  }

  async function fetchLibrary() {
    if (inFlightFetch) {
      return inFlightFetch
    }

    loading.value = true
    error.value = null
    inFlightFetch = (async () => {
      try {
        const serverList = await apiService.getLibrary()
        // Always trust server data for wanted status so the store stays aligned with API semantics.
        audiobooks.value = serverList.map(normalizeLibraryImageUrl)
      } catch (err) {
        error.value = err instanceof Error ? err.message : 'Failed to fetch library'
        errorTracking.captureException(err as Error, {
          component: 'LibraryStore',
          operation: 'fetchLibrary',
        })
      } finally {
        loading.value = false
        inFlightFetch = null
      }
    })()

    return inFlightFetch
  }

  async function removeFromLibrary(
    id: number,
    options?: {
      deleteFiles?: boolean
      deleteFolder?: boolean
      retryAfterBlockedMutation?: (error: unknown) => Promise<boolean | 'cancel'>
    },
  ) {
    const title = audiobooks.value.find((book) => book.id === id)?.title || `Audiobook ${id}`
    const operationId = deleteOperationsStore.beginSingle(id, title)
    try {
      const apiOptions = {
        deleteFiles: options?.deleteFiles,
        deleteFolder: options?.deleteFolder,
      }
      try {
        await apiService.removeFromLibrary(id, apiOptions)
      } catch (initialError: unknown) {
        const retryDecision = options?.retryAfterBlockedMutation
          ? await options.retryAfterBlockedMutation(initialError)
          : false
        if (retryDecision === 'cancel') {
          deleteOperationsStore.cancelSingle(operationId)
          return null
        }
        if (retryDecision !== true) throw initialError
        await apiService.removeFromLibrary(id, apiOptions)
      }
      deleteOperationsStore.completeSingle(operationId)
      // Remove from local state
      audiobooks.value = audiobooks.value.filter((book) => book.id !== id)
      // Remove from selection if selected
      selectedIds.value.delete(id)
      return true
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to remove audiobook'
      error.value = message
      deleteOperationsStore.failSingle(operationId, message)
      errorTracking.captureException(err as Error, {
        component: 'LibraryStore',
        operation: 'removeFromLibrary',
        metadata: { audiobookId: id },
      })
      return false
    }
  }

  async function bulkRemoveFromLibrary(ids: number[]) {
    const uniqueIds = [...new Set(ids)]
    if (uniqueIds.length === 0) return { success: false, deletedCount: 0 }

    const titleById = new Map(
      audiobooks.value.map((book) => [book.id, book.title || `Audiobook ${book.id}`]),
    )
    const operationId = deleteOperationsStore.beginBulk(uniqueIds.length)
    const deletedIds: number[] = []

    try {
      // Perform safe per-id removes while exposing aggregate progress in Notifications.
      for (const id of uniqueIds) {
        const title = titleById.get(id) || `Audiobook ${id}`
        deleteOperationsStore.setBulkCurrentItem(operationId, title)
        try {
          await apiService.removeFromLibrary(id)
          deletedIds.push(id)
          deleteOperationsStore.updateBulkItem(operationId, title, true)
        } catch (err) {
          const message = err instanceof Error ? err.message : `Failed to remove ${title}`
          deleteOperationsStore.updateBulkItem(operationId, title, false, message)
          // Continue attempting remaining deletions even if one fails.
        }
      }

      deleteOperationsStore.finishBulk(operationId)
      // Remove only successfully deleted rows from local state.
      const deletedIdSet = new Set(deletedIds)
      audiobooks.value = audiobooks.value.filter((book) => !deletedIdSet.has(book.id))
      clearSelection()
      return { success: deletedIds.length > 0, deletedCount: deletedIds.length }
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to bulk remove audiobooks'
      error.value = message
      deleteOperationsStore.finishBulk(operationId)
      errorTracking.captureException(err as Error, {
        component: 'LibraryStore',
        operation: 'bulkRemoveFromLibrary',
        metadata: { count: uniqueIds.length },
      })
      return { success: false, deletedCount: deletedIds.length }
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

    const currentFileCount =
      typeof book.fileCount === 'number'
        ? book.fileCount
        : Array.isArray(book.files)
          ? book.files.length
          : 0
    const nextFileCount = Array.isArray(book.files)
      ? newFiles.length
      : Math.max(0, currentFileCount - removed.length)

    // Clone the audiobook object and update files safely so reactivity notices the change
    const updated: Audiobook = {
      ...book,
      files: Array.isArray(book.files) ? newFiles : undefined,
      fileCount: nextFileCount,
      wanted: Boolean(book.monitored) && nextFileCount === 0,
    }

    // If the current primary filePath was one of the removed paths, clear it (safe behavior)
    if (book.filePath) {
      const removedPaths = removed.map((r) => r.path).filter(Boolean) as string[]
      if (removedPaths.includes(book.filePath)) {
        updated.filePath = undefined
        updated.fileSize = undefined
      }
    }

    if (nextFileCount === 0) {
      updated.status = 'no-file'
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
