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
import { ref, computed, shallowRef, triggerRef } from 'vue'
import type { Download, SearchResult, Audiobook } from '@/types'
import { apiService } from '@/services/api'
import { useLibraryStore } from '@/stores/library'
import { signalRService } from '@/services/signalr'
import { errorTracking } from '@/services/errorTracking'
import { logger } from '@/utils/logger'
import { normalizeQueueSnapshot } from '@/utils/queueSnapshot'

export const useDownloadsStore = defineStore('downloads', () => {
  const downloads = shallowRef<Download[]>([])
  const isLoading = ref(false)
  const queueOnlyIds = new Set<string>()
  let unsubscribeUpdate: (() => void) | null = null
  let unsubscribeList: (() => void) | null = null
  let unsubscribeQueue: (() => void) | null = null
  let unsubscribeAudiobook: (() => void) | null = null

  const normalizeDownloadStatus = (status: string | undefined): Download['status'] => {
    const normalized = (status || '').trim().toLowerCase()
    if (normalized === 'queued') return 'Queued'
    if (normalized === 'downloading') return 'Downloading'
    if (normalized === 'paused') return 'Paused'
    if (normalized === 'completed') return 'Completed'
    if (normalized === 'failed') return 'Failed'
    if (normalized === 'processing') return 'Processing'
    if (normalized === 'ready') return 'Ready'
    if (normalized === 'moved') return 'Moved'
    if (normalized === 'importpending') return 'ImportPending'
    if (normalized === 'importblocked') return 'ImportBlocked'
    return 'Queued'
  }

  // Subscribe to SignalR updates
  const initializeSignalR = () => {
    // Subscribe to individual download updates
    unsubscribeUpdate = signalRService.onDownloadUpdate(async (updatedDownloads) => {
      updatedDownloads.forEach((updated) => {
        const index = downloads.value.findIndex((d) => d.id === updated.id)
        if (index !== -1) {
          // Update existing download
          downloads.value[index] = updated
        } else {
          // Add new download
          downloads.value.unshift(updated)
        }

        queueOnlyIds.delete(updated.id)
      })
      triggerRef(downloads)

      // If any updated downloads are completed and linked to an audiobook, refresh that audiobook in the library store
      // Refresh linked audiobooks so UI shows newly-created files. Use Promise.all to avoid unbounded concurrency.
      const libraryStore = useLibraryStore()
      const tasks: Promise<void>[] = []
      for (const updated of updatedDownloads) {
        const status = (updated.status || '').toString().toLowerCase()
        if (
          (status === 'completed' || status === 'ready' || status === 'importpending') &&
          typeof updated.audiobookId === 'number'
        ) {
          const aid = updated.audiobookId as number
          tasks.push(
            (async () => {
              try {
                const latest = await apiService.getAudiobook(aid)
                // Find existing index
                const idx = libraryStore.audiobooks.findIndex((b) => b.id === latest.id)
                if (idx !== -1) {
                  // Preserve the original object reference so components bound to it update reactively
                  const target = libraryStore.audiobooks[idx]!
                  Object.assign(target, latest)
                } else {
                  libraryStore.audiobooks.unshift(latest)
                }
              } catch (e) {
                logger.warn(
                  '[Downloads Store] Failed to refresh audiobook after download update',
                  e,
                )
              }
            })(),
          )
        }
      }
      if (tasks.length > 0) await Promise.all(tasks)

      // Sort by start date
      downloads.value.sort(
        (a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime(),
      )
      triggerRef(downloads)
    })

    // Subscribe to full downloads list
    unsubscribeList = signalRService.onDownloadsList((downloadsList) => {
      downloads.value = downloadsList
      queueOnlyIds.clear()
      triggerRef(downloads)
    })

    // Subscribe to queue updates (replacement list from backend)
    unsubscribeQueue = signalRService.onQueueUpdate((queuePayload) => {
      const queueItems = normalizeQueueSnapshot(queuePayload).items
      const queueIds = new Set(queueItems.map((item) => item.id))

      if (queueOnlyIds.size > 0) {
        downloads.value = downloads.value.filter((download) => {
          if (!queueOnlyIds.has(download.id)) {
            return true
          }

          return queueIds.has(download.id)
        })

        for (const queueOnlyId of Array.from(queueOnlyIds)) {
          if (!queueIds.has(queueOnlyId)) {
            queueOnlyIds.delete(queueOnlyId)
          }
        }
      }

      // QueueUpdate provides the current queue state
      // Do not remove existing tracked downloads solely because they are missing
      // from a single queue snapshot. External clients can briefly report empty
      // queues, and terminal/queue-less states are persisted in DB.

      // Update existing and add new items from queue
      queueItems.forEach((queueItem) => {
        const existingIndex = downloads.value.findIndex((d) => d.id === queueItem.id)

        // Map QueueItem to Download format
        const downloadItem: Download = {
          id: queueItem.id,
          title: queueItem.title || 'Unknown',
          artist: queueItem.author || '',
          album: queueItem.series || '',
          originalUrl: '',
          status: normalizeDownloadStatus(queueItem.status),
          progress: queueItem.progress || 0,
          totalSize: queueItem.size || 0,
          downloadedSize: queueItem.downloaded || 0,
          downloadPath: queueItem.remotePath || '',
          finalPath: queueItem.localPath || '',
          startedAt: queueItem.addedAt,
          completedAt: undefined,
          errorMessage: queueItem.errorMessage,
          downloadClientId: queueItem.downloadClientId,
          metadata: {},
        }

        if (existingIndex !== -1) {
          if (queueOnlyIds.has(queueItem.id)) {
            downloads.value[existingIndex] = downloadItem
          } else {
            const existingDownload = downloads.value[existingIndex]!
            downloads.value[existingIndex] = {
              ...existingDownload,
              progress: downloadItem.progress,
              totalSize: downloadItem.totalSize,
              downloadedSize: downloadItem.downloadedSize,
              downloadPath: downloadItem.downloadPath || existingDownload.downloadPath,
              finalPath: downloadItem.finalPath || existingDownload.finalPath,
              startedAt: downloadItem.startedAt || existingDownload.startedAt,
              errorMessage: downloadItem.errorMessage ?? existingDownload.errorMessage,
              downloadClientId: downloadItem.downloadClientId || existingDownload.downloadClientId,
              status: downloadItem.status,
            }
          }
        } else {
          downloads.value.push(downloadItem)
          queueOnlyIds.add(downloadItem.id)
        }
      })

      triggerRef(downloads)
    })

    // Subscribe to AudiobookUpdate messages so we can apply updated audiobook (with Files)
    unsubscribeAudiobook = signalRService.onAudiobookUpdate((updatedAudiobook) => {
      try {
        const libraryStore = useLibraryStore()
        const idx = libraryStore.audiobooks.findIndex((b) => b.id === updatedAudiobook.id)
        if (idx !== -1) {
          const target = libraryStore.audiobooks[idx] as Audiobook
          Object.assign(target, updatedAudiobook)
        } else {
          libraryStore.audiobooks.unshift(updatedAudiobook)
        }
      } catch (e) {
        logger.warn('[Downloads Store] Failed to apply AudiobookUpdate', e)
      }
    })
    // audiobook unsubscribe will be cleaned up in the common cleanup below
  }

  // Initialize SignalR connection
  initializeSignalR()

  const activeDownloads = computed(() => {
    const active = downloads.value.filter((d) => {
      const status = (d.status || '').toString().toLowerCase()
      const isActive = ['queued', 'downloading', 'paused', 'processing', 'importpending'].includes(
        status,
      )
      return isActive
    })
    return active
  })

  const completedDownloads = computed(() =>
    downloads.value.filter((d) => {
      const status = (d.status || '').toString().toLowerCase()
      return status === 'completed' || status === 'ready'
    }),
  )

  const failedDownloads = computed(() =>
    downloads.value.filter((d) => {
      const status = (d.status || '').toString().toLowerCase()
      return status === 'failed' || status === 'importblocked'
    }),
  )

  const loadDownloads = async () => {
    isLoading.value = true
    try {
      const downloadList = await apiService.getDownloads()
      downloads.value = downloadList
      queueOnlyIds.clear()
      triggerRef(downloads)
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'DownloadsStore',
        operation: 'loadDownloads',
      })
    } finally {
      isLoading.value = false
    }
  }

  const startDownload = async (searchResult: SearchResult, downloadClientId: string) => {
    try {
      const downloadId = await apiService.startDownload(searchResult, downloadClientId)
      // No need to manually refresh - SignalR will push the update
      return downloadId
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'DownloadsStore',
        operation: 'startDownload',
        metadata: { title: searchResult.title, downloadClientId },
      })
      throw error
    }
  }

  const cancelDownload = async (downloadId: string) => {
    try {
      await apiService.cancelDownload(downloadId)
      // No need to manually refresh - SignalR will push the update
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'DownloadsStore',
        operation: 'cancelDownload',
        metadata: { downloadId },
      })
      throw error
    }
  }

  const retryBlockedImport = async (downloadId: string) => {
    try {
      return await apiService.retryBlockedImport(downloadId)
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'DownloadsStore',
        operation: 'retryBlockedImport',
        metadata: { downloadId },
      })
      throw error
    }
  }

  const updateDownload = (updatedDownload: Download) => {
    const index = downloads.value.findIndex((d) => d.id === updatedDownload.id)
    if (index !== -1) {
      downloads.value[index] = updatedDownload
    }

    queueOnlyIds.delete(updatedDownload.id)
  }

  // Cleanup on store destruction
  const cleanup = () => {
    if (unsubscribeUpdate) unsubscribeUpdate()
    if (unsubscribeList) unsubscribeList()
    if (unsubscribeQueue) unsubscribeQueue()
    if (unsubscribeAudiobook) unsubscribeAudiobook()
  }

  return {
    downloads,
    isLoading,
    activeDownloads,
    completedDownloads,
    failedDownloads,
    loadDownloads,
    startDownload,
    cancelDownload,
    retryBlockedImport,
    updateDownload,
    cleanup,
  }
})
