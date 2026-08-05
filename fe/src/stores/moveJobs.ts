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
import { defineStore } from 'pinia'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { useToast } from '@/services/toastService'
import { logger } from '@/utils/logger'

export type MoveJobStatus =
  | 'Queued'
  | 'Running'
  | 'RetryScheduled'
  | 'NeedsAttention'
  | 'Completed'
  | 'Failed'
  | 'Superseded'

export interface TrackedMoveJob {
  jobId: string
  audiobookId?: number
  status: MoveJobStatus
  target?: string
  error?: string
  recoveryDisposition?: string
  canRetry?: boolean
}

export interface MoveRecoveryState {
  hasUnresolvedMove: boolean
  disposition: string
  jobId?: string
  status?: MoveJobStatus
  phase?: string
  requestedPath?: string
  error?: string
  canRetry: boolean
  blockingJobIds: string[]
}

type MoveJobUpdate = {
  jobId?: string
  audiobookId?: number
  status?: string
  target?: string
  error?: string
  recoveryDisposition?: string
  canRetry?: boolean
}

const terminalStatuses = new Set<MoveJobStatus>([
  'Completed',
  'Failed',
  'NeedsAttention',
  'Superseded',
])

function normalizeStatus(status: string | undefined): MoveJobStatus | null {
  switch ((status || '').trim().toLowerCase()) {
    case 'running':
      return 'Running'
    case 'retryscheduled':
      return 'RetryScheduled'
    case 'needsattention':
      return 'NeedsAttention'
    case 'completed':
      return 'Completed'
    case 'failed':
      return 'Failed'
    case 'superseded':
      return 'Superseded'
    case 'queued':
      return 'Queued'
    default:
      return null
  }
}

function normalizeJobId(jobId: string): string {
  return jobId.trim().toLowerCase()
}

export const useMoveJobsStore = defineStore('moveJobs', () => {
  const trackedById = ref<Record<string, TrackedMoveJob>>({})
  const toast = useToast()
  let unsubscribe: (() => void) | null = null

  const trackedJobs = computed(() => Object.values(trackedById.value))

  function getActiveJobForAudiobook(audiobookId: number): TrackedMoveJob | undefined {
    return trackedJobs.value.find(
      (job) => job.audiobookId === audiobookId && !terminalStatuses.has(job.status),
    )
  }

  async function getRecoveryStateForAudiobook(audiobookId: number): Promise<MoveRecoveryState> {
    const response = await apiService.getMoveRecoveryState(audiobookId)
    const status = response.status ? normalizeStatus(response.status) : null
    return {
      hasUnresolvedMove: Boolean(response.hasUnresolvedMove),
      disposition: response.disposition,
      jobId: response.jobId || undefined,
      status: status ?? undefined,
      phase: response.phase || undefined,
      requestedPath: response.requestedPath || undefined,
      error: response.error || undefined,
      canRetry: Boolean(response.canRetry),
      blockingJobIds: response.blockingJobIds || [],
    }
  }

  async function requeueMoveJob(
    jobId: string,
    audiobookId?: number,
    target?: string,
  ): Promise<string> {
    const response = await apiService.requeueMoveJob(jobId)
    const requeuedJobId = response.jobId?.trim()
    if (!requeuedJobId) {
      throw new Error('The server did not return a durable move job ID.')
    }

    trackQueuedJob({
      jobId: requeuedJobId,
      audiobookId,
      target,
      status: 'Queued',
    })
    return requeuedJobId
  }

  function start() {
    if (unsubscribe) {
      return
    }

    unsubscribe = signalRService.onMoveJobUpdate(handleMoveJobUpdate)
  }

  function stop() {
    if (unsubscribe) {
      try {
        unsubscribe()
      } catch (error) {
        logger.debug('Failed to unsubscribe from move job updates', error)
      }
    }

    unsubscribe = null
  }

  function trackQueuedJob(job: {
    jobId: string
    audiobookId?: number
    target?: string
    status?: MoveJobStatus
  }) {
    if (!job.jobId?.trim()) {
      return
    }

    start()
    const key = normalizeJobId(job.jobId)
    trackedById.value[key] = {
      jobId: job.jobId,
      audiobookId: job.audiobookId,
      status: job.status ?? 'Queued',
      target: job.target,
    }
    void reconcileTrackedJob(key, job.jobId)
  }

  async function reconcileTrackedJob(key: string, jobId: string) {
    try {
      const current = await apiService.getMoveJobStatus(jobId)
      const existing = trackedById.value[key]
      if (!existing) {
        return
      }

      const currentStatus = normalizeStatus(current.status)
      if (currentStatus == null) {
        return
      }
      if (existing.status !== 'Queued' && !terminalStatuses.has(currentStatus)) {
        return
      }

      handleMoveJobUpdate(current)
    } catch {
      // SignalR remains authoritative when a one-time reconciliation request fails.
    }
  }

  function handleMoveJobUpdate(update: MoveJobUpdate) {
    if (!update.jobId?.trim()) {
      return
    }

    const key = normalizeJobId(update.jobId)
    const existing = trackedById.value[key]
    if (!existing) {
      return
    }

    const status = normalizeStatus(update.status)
    if (status == null) {
      return
    }

    const next: TrackedMoveJob = {
      ...existing,
      audiobookId: update.audiobookId ?? existing.audiobookId,
      status,
      target: update.target ?? existing.target,
      error: update.error,
      recoveryDisposition: update.recoveryDisposition ?? existing.recoveryDisposition,
      canRetry: update.canRetry ?? existing.canRetry,
    }
    trackedById.value[key] = next

    if (status === 'Running' && existing.status !== 'Running') {
      toast.info('Move in progress', `Moving files to ${next.target || 'selected destination'}`)
      return
    }

    if (!terminalStatuses.has(status)) {
      return
    }

    if (status === 'Completed') {
      toast.success('Move completed', `Files moved to ${next.target || 'selected destination'}`)
    } else if (status === 'Superseded') {
      toast.info('Move superseded', 'A newer library state replaced this queued move.')
    } else {
      toast.error(
        status === 'NeedsAttention' ? 'Move needs attention' : 'Move failed',
        next.error || 'Move job did not complete. Check the move queue.',
      )
    }

    delete trackedById.value[key]
  }

  return {
    trackedJobs,
    trackedById,
    getActiveJobForAudiobook,
    getRecoveryStateForAudiobook,
    requeueMoveJob,
    start,
    stop,
    trackQueuedJob,
    handleMoveJobUpdate,
  }
})
