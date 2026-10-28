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
  progress: number
  phase?: string
  target?: string
  error?: string
  recoveryDisposition?: string
  canRetry?: boolean
  sourceRetained?: boolean
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
  progress?: number
  phase?: string
  target?: string
  error?: string
  recoveryDisposition?: string
  canRetry?: boolean
  sourceRetained?: boolean
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

function normalizeProgress(progress: number | undefined, fallback: number): number {
  if (progress == null || !Number.isFinite(progress)) {
    return fallback
  }

  return Math.min(100, Math.max(0, progress))
}

export const useMoveJobsStore = defineStore('moveJobs', () => {
  const trackedById = ref<Record<string, TrackedMoveJob>>({})
  const toast = useToast()
  let unsubscribe: (() => void) | null = null
  let evidenceClock = 0
  let activeRefreshGeneration = 0
  const evidenceVersionById = new Map<string, number>()

  const trackedJobs = computed(() => Object.values(trackedById.value))

  function getEvidenceVersion(key: string): number {
    return evidenceVersionById.get(key) ?? 0
  }

  function markEvidence(key: string): void {
    evidenceClock += 1
    evidenceVersionById.set(key, evidenceClock)
  }

  function setTrackedJob(key: string, job: TrackedMoveJob): void {
    trackedById.value[key] = job
    markEvidence(key)
  }

  function removeTrackedJob(key: string): void {
    delete trackedById.value[key]
    markEvidence(key)
  }

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
    void loadActiveJobs()
  }

  async function loadActiveJobs() {
    try {
      // Reconcile only jobs that were already tracked when this authoritative
      // active-job snapshot began. A newly queued job can be added locally while
      // the request is in flight and must not be pruned from an older snapshot.
      const refreshGeneration = ++activeRefreshGeneration
      const refreshEvidenceVersion = evidenceClock
      const trackedBeforeRefresh = new Set(Object.keys(trackedById.value))
      const jobs = await apiService.getActiveMoveJobs()
      if (refreshGeneration !== activeRefreshGeneration) {
        return
      }

      const activeKeys = new Set<string>()
      for (const job of jobs) {
        if (!job.jobId?.trim()) {
          continue
        }

        const status = normalizeStatus(job.status)
        if (status == null || terminalStatuses.has(status)) {
          continue
        }

        const key = normalizeJobId(job.jobId)
        activeKeys.add(key)
        if (getEvidenceVersion(key) > refreshEvidenceVersion) {
          continue
        }

        const existing = trackedById.value[key]
        setTrackedJob(key, {
          jobId: job.jobId,
          audiobookId: job.audiobookId ?? existing?.audiobookId,
          status,
          progress: normalizeProgress(job.progress, existing?.progress ?? 0),
          phase: job.phase ?? existing?.phase,
          target: job.target ?? existing?.target,
          error: job.error,
          recoveryDisposition: job.recoveryDisposition ?? existing?.recoveryDisposition,
          canRetry: job.canRetry ?? existing?.canRetry,
        })
      }

      for (const key of trackedBeforeRefresh) {
        if (activeKeys.has(key)) {
          continue
        }

        const existing = trackedById.value[key]
        if (!existing || getEvidenceVersion(key) > refreshEvidenceVersion) {
          continue
        }

        const lookupEvidenceVersion = getEvidenceVersion(key)
        try {
          const current = await apiService.getMoveJobStatus(existing.jobId)
          if (
            refreshGeneration !== activeRefreshGeneration ||
            getEvidenceVersion(key) !== lookupEvidenceVersion
          ) {
            continue
          }

          const currentStatus = normalizeStatus(current.status)
          if (currentStatus != null && terminalStatuses.has(currentStatus)) {
            handleMoveJobUpdate(current)
            continue
          }
          if (currentStatus != null && !terminalStatuses.has(currentStatus)) {
            // A status read taken after the active snapshot is newer evidence.
            // Preserve the job and let the next refresh reconcile it.
            continue
          }
        } catch {
          // The active-list request succeeded and is authoritative for whether the
          // job remains active. If its terminal detail lookup fails, drop the stale
          // local entry rather than displaying a move forever after reconnect.
        }

        if (
          refreshGeneration === activeRefreshGeneration &&
          getEvidenceVersion(key) === lookupEvidenceVersion
        ) {
          removeTrackedJob(key)
        }
      }
    } catch (error) {
      logger.debug('Failed to load active move jobs', error)
    }
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
    setTrackedJob(key, {
      jobId: job.jobId,
      audiobookId: job.audiobookId,
      status: job.status ?? 'Queued',
      progress: job.status === 'Completed' ? 100 : 0,
      target: job.target,
    })
    void reconcileTrackedJob(key, job.jobId)
  }

  async function reconcileTrackedJob(key: string, jobId: string) {
    const lookupEvidenceVersion = getEvidenceVersion(key)
    try {
      const current = await apiService.getMoveJobStatus(jobId)
      if (getEvidenceVersion(key) !== lookupEvidenceVersion) {
        return
      }

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
    const status = normalizeStatus(update.status)
    if (status == null) {
      return
    }

    const existing = trackedById.value[key]
    if (!existing) {
      if (terminalStatuses.has(status)) {
        markEvidence(key)
      }
      return
    }

    const next: TrackedMoveJob = {
      ...existing,
      audiobookId: update.audiobookId ?? existing.audiobookId,
      status,
      progress: normalizeProgress(
        update.progress,
        status === 'Completed' ? 100 : existing.progress,
      ),
      phase: update.phase ?? existing.phase,
      target: update.target ?? existing.target,
      error: update.error,
      recoveryDisposition: update.recoveryDisposition ?? existing.recoveryDisposition,
      canRetry: update.canRetry ?? existing.canRetry,
      sourceRetained: update.sourceRetained ?? existing.sourceRetained,
    }
    setTrackedJob(key, next)

    if (status === 'Running' && existing.status !== 'Running') {
      toast.info('Move in progress', `Moving files to ${next.target || 'selected destination'}`)
      return
    }

    if (!terminalStatuses.has(status)) {
      return
    }

    if (status === 'Completed') {
      toast.success(
        next.sourceRetained ? 'Copy completed' : 'Move completed',
        next.sourceRetained
          ? `Files copied to ${next.target || 'selected destination'}; source retained`
          : `Files moved to ${next.target || 'selected destination'}`,
      )
    } else if (status === 'Superseded') {
      toast.info('Move superseded', 'A newer library state replaced this queued move.')
    } else {
      toast.error(
        status === 'NeedsAttention' ? 'Move needs attention' : 'Move failed',
        next.error || 'Move job did not complete. Check the move queue.',
      )
    }

    removeTrackedJob(key)
  }

  return {
    trackedJobs,
    trackedById,
    getActiveJobForAudiobook,
    getRecoveryStateForAudiobook,
    requeueMoveJob,
    start,
    stop,
    loadActiveJobs,
    trackQueuedJob,
    handleMoveJobUpdate,
  }
})
