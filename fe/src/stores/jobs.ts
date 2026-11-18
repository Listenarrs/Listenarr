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
import { computed } from 'vue'
import type { RouteLocationRaw } from 'vue-router'
import { useLibraryImportStore } from '@/stores/libraryImport'
import { useMoveJobsStore } from '@/stores/moveJobs'
import { useScanNotificationsStore } from '@/stores/scanNotifications'
import { useLibraryDeleteOperationsStore } from '@/stores/libraryDeleteOperations'
import { useLibraryStore } from '@/stores/library'

export type JobKind = 'library-import-scan' | 'move' | 'folder-scan' | 'delete'
export type JobStatus = 'queued' | 'running' | 'done' | 'failed' | 'canceled'

// One background job, whatever store it comes from: what the Jobs menu and the Jobs page show
export interface JobEntry {
  id: string
  kind: JobKind
  title: string
  message: string
  status: JobStatus
  // Queued or running: shown with a spinner (or its progress), can't be dismissed
  active: boolean
  // A percentage, for the jobs that know one (moves, bulk deletes)
  progress?: number
  startedAt?: string
  // The page the job was started from
  to?: RouteLocationRaw
  cancelable: boolean
  icon: string
}

const LIBRARY_IMPORT_SCAN_JOB_ID = 'library-import-scan'

// The background jobs of the session: the Library Import scan, moves, folder scans and deletes.
// The jobs stay in their own stores; this one only lists them and routes dismiss, clear and cancel.
export const useJobsStore = defineStore('jobs', () => {
  const libraryImportStore = useLibraryImportStore()
  const moveJobsStore = useMoveJobsStore()
  const scanNotificationsStore = useScanNotificationsStore()
  const deleteOperationsStore = useLibraryDeleteOperationsStore()
  const libraryStore = useLibraryStore()

  function bookTitle(audiobookId?: number | null): string | undefined {
    return audiobookId
      ? libraryStore.audiobooks.find((book) => book.id === audiobookId)?.title
      : undefined
  }

  function bookPage(audiobookId?: number | null): RouteLocationRaw | undefined {
    return audiobookId
      ? { name: 'audiobook-detail', params: { id: String(audiobookId) } }
      : undefined
  }

  const libraryImportJobs = computed<JobEntry[]>(() => {
    const task = libraryImportStore.scanTask
    if (!task) return []
    const folder = task.folderName || 'root folder'
    const active = task.status === 'starting' || task.status === 'running'
    const startedAt = new Date(task.startedAt).toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
    })
    const shown = active
      ? {
          status: 'running' as const,
          title: `Scanning ${folder} for unmatched files`,
          message: `Library Import · started at ${startedAt}`,
          icon: 'ph ph-folder-open',
        }
      : task.status === 'done'
        ? {
            status: 'done' as const,
            title: `Scan of ${folder} finished`,
            message:
              task.itemCount === 1 ? '1 unmatched book' : `${task.itemCount ?? 0} unmatched books`,
            icon: 'ph ph-check-circle',
          }
        : task.status === 'failed'
          ? {
              status: 'failed' as const,
              title: `Scan of ${folder} failed`,
              message: task.error || 'The scan did not complete',
              icon: 'ph ph-warning',
            }
          : {
              status: 'canceled' as const,
              title: `Scan of ${folder} canceled`,
              message: 'Library Import',
              icon: 'ph ph-prohibit',
            }
    return [
      {
        id: LIBRARY_IMPORT_SCAN_JOB_ID,
        kind: 'library-import-scan',
        ...shown,
        active,
        startedAt: task.startedAt,
        cancelable: active,
        to: { name: 'library-import', query: { rootFolder: String(task.rootFolderId) } },
      },
    ]
  })

  // A move is tracked while it runs only: its store drops it once it ends
  const moveJobs = computed<JobEntry[]>(() =>
    moveJobsStore.trackedJobs.map((job) => {
      const title = bookTitle(job.audiobookId)
      const target = job.target ? ` to ${job.target}` : ''
      return {
        id: `move-${job.jobId}`,
        kind: 'move',
        title: title ? `Moving ${title}` : 'Moving audiobook',
        message: `${job.phase || 'Preparing move'}${target}`,
        status: job.status === 'Queued' ? 'queued' : 'running',
        active: true,
        progress: job.progress,
        cancelable: false,
        to: bookPage(job.audiobookId),
        icon: 'ph ph-folder-open',
      }
    }),
  )

  const folderScanJobs = computed<JobEntry[]>(() =>
    scanNotificationsStore.jobs
      .filter((job) => job.visible && !job.dismissed)
      .sort((left, right) => right.timestamp.localeCompare(left.timestamp))
      .map((job) => {
        const status = job.status.toLowerCase()
        const subject = bookTitle(job.audiobookId) || 'audiobook folder'
        const shown =
          status === 'queued'
            ? {
                status: 'queued' as const,
                title: `Scan queued: ${subject}`,
                message: 'Waiting to scan folder',
              }
            : status === 'processing'
              ? {
                  status: 'running' as const,
                  title: `Scanning ${subject}`,
                  message: 'Scanning folder',
                }
              : status === 'completed'
                ? {
                    status: 'done' as const,
                    title: `Scan complete: ${subject}`,
                    message:
                      job.found != null
                        ? `${job.found} file${job.found === 1 ? '' : 's'} found${job.created != null ? ` · ${job.created} added` : ''}`
                        : 'Folder scan completed',
                  }
                : status === 'superseded'
                  ? {
                      status: 'canceled' as const,
                      title: `Scan stopped: ${subject}`,
                      message: job.error || 'The folder scan did not complete',
                    }
                  : {
                      status: 'failed' as const,
                      title: `Scan failed: ${subject}`,
                      message: job.error || 'The folder scan did not complete',
                    }
        return {
          id: `scan-${job.jobId}`,
          kind: 'folder-scan' as const,
          ...shown,
          active: shown.status === 'queued' || shown.status === 'running',
          startedAt: job.timestamp,
          cancelable: false,
          to: bookPage(job.audiobookId),
          icon: 'ph ph-folder-open',
        }
      }),
  )

  const deleteJobs = computed<JobEntry[]>(() =>
    deleteOperationsStore.operations
      .filter((operation) => !operation.dismissed)
      .map((operation) => {
        const active = operation.status === 'deleting'
        const isBulk = operation.kind === 'bulk'
        const title = active
          ? isBulk
            ? operation.title
            : `Deleting ${operation.title}`
          : operation.status === 'completed'
            ? isBulk
              ? `Deleted ${operation.deleted} audiobook${operation.deleted === 1 ? '' : 's'}`
              : `Deleted ${operation.title}`
            : isBulk
              ? `Delete incomplete: ${operation.deleted}/${operation.total} audiobooks`
              : `Delete failed: ${operation.title}`
        const message = isBulk
          ? active
            ? operation.currentTitle
              ? `${operation.processed}/${operation.total} · ${operation.currentTitle}`
              : `${operation.processed}/${operation.total}`
            : operation.status === 'completed'
              ? `${operation.deleted}/${operation.total} deleted`
              : `${operation.deleted}/${operation.total} deleted · ${operation.failed} failed${operation.error ? ` · ${operation.error}` : ''}`
          : active
            ? 'Removing audiobook from library'
            : operation.status === 'completed'
              ? 'Removed from library'
              : operation.error || 'Could not remove audiobook from library'
        return {
          id: operation.id,
          kind: 'delete' as const,
          title,
          message,
          status: active
            ? ('running' as const)
            : operation.status === 'completed'
              ? ('done' as const)
              : ('failed' as const),
          active,
          // A single delete has no meaningful percentage
          progress: active && isBulk ? operation.progress : undefined,
          startedAt: operation.startedAt,
          cancelable: false,
          icon: 'ph ph-file-remove',
        }
      }),
  )

  const jobs = computed<JobEntry[]>(() => [
    ...libraryImportJobs.value,
    ...moveJobs.value,
    ...folderScanJobs.value,
    ...deleteJobs.value,
  ])

  const hasActiveJobs = computed(() => jobs.value.some((job) => job.active))

  function dismiss(id: string) {
    if (id === LIBRARY_IMPORT_SCAN_JOB_ID) libraryImportStore.dismissScanTask()
    else if (id.startsWith('scan-')) scanNotificationsStore.dismiss(id.slice('scan-'.length))
    else deleteOperationsStore.dismiss(id)
  }

  // The jobs that ended are forgotten; the running ones stay
  function clearFinished() {
    libraryImportStore.dismissScanTask()
    scanNotificationsStore.clearFinished()
    deleteOperationsStore.clearFinished()
  }

  async function cancel(id: string) {
    if (id === LIBRARY_IMPORT_SCAN_JOB_ID) await libraryImportStore.cancelScan()
  }

  return { jobs, hasActiveJobs, dismiss, clearFinished, cancel }
})
