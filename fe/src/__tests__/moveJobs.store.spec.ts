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
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

type MoveJobUpdate = {
  jobId?: string
  audiobookId?: number
  status?: string
  progress?: number
  phase?: string
  target?: string
  error?: string
}

const toastMocks = vi.hoisted(() => ({
  info: vi.fn(),
  success: vi.fn(),
  error: vi.fn(),
}))

const apiMocks = vi.hoisted(() => ({
  getActiveMoveJobs: vi.fn(),
  getMoveJobStatus: vi.fn(),
}))

const signalRMocks = vi.hoisted(() => {
  const state = {
    callback: null as ((job: MoveJobUpdate) => void) | null,
    unsubscribe: vi.fn(),
    onMoveJobUpdate: vi.fn(),
  }
  state.onMoveJobUpdate.mockImplementation((callback: (job: MoveJobUpdate) => void) => {
    state.callback = callback
    return state.unsubscribe
  })
  return state
})

vi.mock('@/services/api', () => ({
  apiService: {
    getActiveMoveJobs: apiMocks.getActiveMoveJobs,
    getMoveJobStatus: apiMocks.getMoveJobStatus,
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => toastMocks,
}))

vi.mock('@/services/signalr', () => ({
  signalRService: {
    onMoveJobUpdate: signalRMocks.onMoveJobUpdate,
  },
}))

import { useMoveJobsStore } from '@/stores/moveJobs'

describe('move jobs store', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setActivePinia(createPinia())
    signalRMocks.callback = null
    apiMocks.getActiveMoveJobs.mockResolvedValue([])
    apiMocks.getMoveJobStatus.mockImplementation(() => new Promise(() => {}))
    signalRMocks.onMoveJobUpdate.mockImplementation((callback: (job: MoveJobUpdate) => void) => {
      signalRMocks.callback = callback
      return signalRMocks.unsubscribe
    })
  })

  it('starts SignalR subscription idempotently', () => {
    const store = useMoveJobsStore()

    store.start()
    store.start()

    expect(signalRMocks.onMoveJobUpdate).toHaveBeenCalledTimes(1)

    store.stop()
    expect(signalRMocks.unsubscribe).toHaveBeenCalledTimes(1)
  })

  it('recovers active move jobs when the store starts', async () => {
    apiMocks.getActiveMoveJobs.mockResolvedValue([
      {
        jobId: 'job-active',
        audiobookId: 42,
        status: 'Running',
        progress: 61.5,
        phase: 'Copying',
        target: '/library/book',
      },
    ])
    const store = useMoveJobsStore()

    store.start()

    await vi.waitFor(() =>
      expect(store.trackedById['job-active']).toMatchObject({
        status: 'Running',
        progress: 61.5,
        phase: 'Copying',
      }),
    )
  })

  it('recovers a missed terminal update when a tracked job disappears from the active snapshot', async () => {
    apiMocks.getActiveMoveJobs
      .mockResolvedValueOnce([
        {
          jobId: 'job-1',
          audiobookId: 42,
          status: 'Running',
          progress: 40,
          target: '/library/book',
        },
      ])
      .mockResolvedValueOnce([])
    apiMocks.getMoveJobStatus.mockResolvedValue({
      jobId: 'job-1',
      audiobookId: 42,
      status: 'Completed',
      progress: 100,
      target: '/library/book',
    })
    const store = useMoveJobsStore()

    store.start()
    await vi.waitFor(() => expect(store.trackedById['job-1']?.status).toBe('Running'))

    await store.loadActiveJobs()

    expect(apiMocks.getMoveJobStatus).toHaveBeenCalledWith('job-1')
    expect(toastMocks.success).toHaveBeenCalledWith(
      'Move completed',
      'Files moved to /library/book',
    )
    expect(store.trackedById['job-1']).toBeUndefined()
  })

  it('preserves a job when a newer status read still reports it active', async () => {
    apiMocks.getActiveMoveJobs
      .mockResolvedValueOnce([
        {
          jobId: 'job-1',
          audiobookId: 42,
          status: 'Running',
          progress: 40,
          target: '/library/book',
        },
      ])
      .mockResolvedValueOnce([])
    apiMocks.getMoveJobStatus.mockResolvedValue({
      jobId: 'job-1',
      audiobookId: 42,
      status: 'Running',
      progress: 55,
      target: '/library/book',
    })
    const store = useMoveJobsStore()

    store.start()
    await vi.waitFor(() => expect(store.trackedById['job-1']?.status).toBe('Running'))

    await store.loadActiveJobs()

    expect(store.trackedById['job-1']?.status).toBe('Running')
    expect(toastMocks.success).not.toHaveBeenCalled()
    expect(toastMocks.error).not.toHaveBeenCalled()
  })

  it('does not resurrect a terminal job from an older in-flight active snapshot', async () => {
    let resolveActive:
      | ((
          jobs: Array<{
            jobId: string
            audiobookId: number
            status: string
            progress: number
            target: string
          }>,
        ) => void)
      | undefined
    apiMocks.getActiveMoveJobs.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveActive = resolve
        }),
    )
    const store = useMoveJobsStore()
    store.trackQueuedJob({ jobId: 'job-1', audiobookId: 42, target: '/library/book' })

    const refresh = store.loadActiveJobs()
    signalRMocks.callback?.({
      jobId: 'job-1',
      audiobookId: 42,
      status: 'Completed',
      progress: 100,
      target: '/library/book',
    })
    resolveActive?.([
      {
        jobId: 'job-1',
        audiobookId: 42,
        status: 'Running',
        progress: 75,
        target: '/library/book',
      },
    ])
    await refresh

    expect(store.trackedById['job-1']).toBeUndefined()
    expect(toastMocks.success).toHaveBeenCalledTimes(1)
  })

  it('ignores an older active snapshot when a newer refresh finishes first', async () => {
    let resolveOlder:
      | ((
          jobs: Array<{
            jobId: string
            audiobookId: number
            status: string
            progress: number
            target: string
          }>,
        ) => void)
      | undefined
    apiMocks.getActiveMoveJobs
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveOlder = resolve
          }),
      )
      .mockResolvedValueOnce([])
    const store = useMoveJobsStore()

    const olderRefresh = store.loadActiveJobs()
    await store.loadActiveJobs()
    resolveOlder?.([
      {
        jobId: 'job-old',
        audiobookId: 42,
        status: 'Running',
        progress: 50,
        target: '/library/book',
      },
    ])
    await olderRefresh

    expect(store.trackedById['job-old']).toBeUndefined()
  })

  it('does not prune a newly tracked job from an older in-flight active snapshot', async () => {
    let resolveActive: ((jobs: never[]) => void) | undefined
    apiMocks.getActiveMoveJobs.mockImplementationOnce(
      () =>
        new Promise<never[]>((resolve) => {
          resolveActive = resolve
        }),
    )
    const store = useMoveJobsStore()

    const refresh = store.loadActiveJobs()
    store.trackQueuedJob({ jobId: 'job-new', target: '/library/new' })
    resolveActive?.([])
    await refresh

    expect(store.trackedById['job-new']?.status).toBe('Queued')
  })

  it('tracks queued move jobs and subscribes on first track', () => {
    const store = useMoveJobsStore()

    store.trackQueuedJob({
      jobId: 'JOB-1',
      audiobookId: 42,
      target: '/library/book',
    })

    expect(signalRMocks.onMoveJobUpdate).toHaveBeenCalledTimes(1)
    expect(store.trackedById['job-1']).toEqual({
      jobId: 'JOB-1',
      audiobookId: 42,
      status: 'Queued',
      progress: 0,
      target: '/library/book',
    })
  })

  it('shows one in-progress toast when a tracked job starts running', () => {
    const store = useMoveJobsStore()
    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    signalRMocks.callback?.({ jobId: 'job-1', status: 'Running', target: '/library/book' })
    signalRMocks.callback?.({ jobId: 'job-1', status: 'Running', target: '/library/book' })

    expect(toastMocks.info).toHaveBeenCalledTimes(1)
    expect(toastMocks.info).toHaveBeenCalledWith(
      'Move in progress',
      'Moving files to /library/book',
    )
    expect(store.trackedById['job-1']?.status).toBe('Running')
  })

  it('tracks realtime move progress and phase without repeating the running toast', () => {
    const store = useMoveJobsStore()
    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    signalRMocks.callback?.({
      jobId: 'job-1',
      status: 'Running',
      progress: 18.5,
      phase: 'Copying',
      target: '/library/book',
    })
    signalRMocks.callback?.({
      jobId: 'job-1',
      status: 'Running',
      progress: 63.25,
      phase: 'Copying',
      target: '/library/book',
    })

    expect(store.trackedById['job-1']).toMatchObject({
      status: 'Running',
      progress: 63.25,
      phase: 'Copying',
    })
    expect(toastMocks.info).toHaveBeenCalledTimes(1)
  })

  it('shows success toast and clears tracked job on completion', () => {
    const store = useMoveJobsStore()
    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    signalRMocks.callback?.({ jobId: 'job-1', status: 'Completed', target: '/library/book' })

    expect(toastMocks.success).toHaveBeenCalledWith(
      'Move completed',
      'Files moved to /library/book',
    )
    expect(store.trackedById['job-1']).toBeUndefined()
  })

  it('shows attention toast and clears tracked job on NeedsAttention', () => {
    const store = useMoveJobsStore()
    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    signalRMocks.callback?.({
      jobId: 'job-1',
      status: 'NeedsAttention',
      target: '/library/book',
      error: 'Manual review required',
    })

    expect(toastMocks.error).toHaveBeenCalledWith('Move needs attention', 'Manual review required')
    expect(store.trackedById['job-1']).toBeUndefined()
  })

  it('reconciles a job that completed before tracking began', async () => {
    apiMocks.getMoveJobStatus.mockResolvedValue({
      jobId: 'job-1',
      status: 'Completed',
      target: '/library/book',
    })
    const store = useMoveJobsStore()

    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    await vi.waitFor(() => expect(store.trackedById['job-1']).toBeUndefined())
    expect(toastMocks.success).toHaveBeenCalledWith(
      'Move completed',
      'Files moved to /library/book',
    )
  })

  it('does not recreate a terminal job when a stale status response arrives later', async () => {
    let resolveStatus:
      | ((value: { jobId: string; status: string; target: string }) => void)
      | undefined
    apiMocks.getMoveJobStatus.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveStatus = resolve
        }),
    )
    const store = useMoveJobsStore()
    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    signalRMocks.callback?.({ jobId: 'job-1', status: 'Completed', target: '/library/book' })
    resolveStatus?.({ jobId: 'job-1', status: 'Queued', target: '/library/book' })
    await Promise.resolve()

    expect(store.trackedById['job-1']).toBeUndefined()
    expect(toastMocks.success).toHaveBeenCalledTimes(1)
  })

  it('does not regress a running job when a stale queued status response arrives', async () => {
    let resolveStatus:
      | ((value: { jobId: string; status: string; target: string }) => void)
      | undefined
    apiMocks.getMoveJobStatus.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveStatus = resolve
        }),
    )
    const store = useMoveJobsStore()
    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    signalRMocks.callback?.({ jobId: 'job-1', status: 'Running', target: '/library/book' })
    resolveStatus?.({ jobId: 'job-1', status: 'Queued', target: '/library/book' })
    await Promise.resolve()

    expect(store.trackedById['job-1']?.status).toBe('Running')
    expect(toastMocks.info).toHaveBeenCalledTimes(1)
  })

  it('ignores an unknown realtime status instead of regressing a running job', () => {
    const store = useMoveJobsStore()
    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })
    signalRMocks.callback?.({ jobId: 'job-1', status: 'Running', target: '/library/book' })

    signalRMocks.callback?.({ jobId: 'job-1', status: 'Paused', target: '/library/book' })

    expect(store.trackedById['job-1']?.status).toBe('Running')
    expect(toastMocks.info).toHaveBeenCalledTimes(1)
    expect(toastMocks.success).not.toHaveBeenCalled()
    expect(toastMocks.error).not.toHaveBeenCalled()
  })

  it('ignores an unknown reconciliation status', async () => {
    apiMocks.getMoveJobStatus.mockResolvedValue({
      jobId: 'job-1',
      status: 'Paused',
      target: '/library/book',
    })
    const store = useMoveJobsStore()

    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    await vi.waitFor(() => expect(apiMocks.getMoveJobStatus).toHaveBeenCalledWith('job-1'))
    expect(store.trackedById['job-1']?.status).toBe('Queued')
    expect(toastMocks.info).not.toHaveBeenCalled()
    expect(toastMocks.success).not.toHaveBeenCalled()
    expect(toastMocks.error).not.toHaveBeenCalled()
  })

  it('keeps tracking when status reconciliation fails', async () => {
    apiMocks.getMoveJobStatus.mockRejectedValue(new Error('offline'))
    const store = useMoveJobsStore()

    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    await vi.waitFor(() => expect(apiMocks.getMoveJobStatus).toHaveBeenCalledWith('job-1'))
    expect(store.trackedById['job-1']?.status).toBe('Queued')
    expect(toastMocks.error).not.toHaveBeenCalled()
  })

  it('shows informational terminal toast and clears tracked job on Superseded', () => {
    const store = useMoveJobsStore()
    store.trackQueuedJob({ jobId: 'job-1', target: '/library/book' })

    signalRMocks.callback?.({ jobId: 'job-1', status: 'Superseded', target: '/library/book' })

    expect(toastMocks.info).toHaveBeenCalledWith(
      'Move superseded',
      'A newer library state replaced this queued move.',
    )
    expect(toastMocks.error).not.toHaveBeenCalled()
    expect(store.trackedById['job-1']).toBeUndefined()
  })
})
