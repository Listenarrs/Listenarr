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
import { reactive } from 'vue'
import { useScanNotificationsStore } from '@/stores/scanNotifications'
import { useLibraryDeleteOperationsStore } from '@/stores/libraryDeleteOperations'

type ScanTask = {
  id: string
  jobId: string | null
  rootFolderId: number
  folderName: string | null
  folderPath?: string | null
  status: 'starting' | 'running' | 'done' | 'failed' | 'canceled'
  startedAt: string
  finishedAt: string | null
  error: string | null
  itemCount: number | null
}

const libraryImport = reactive({
  scanTask: null as ScanTask | null,
  scanHistory: [] as ScanTask[],
  dismissScan: vi.fn(),
  clearFinishedScans: vi.fn(),
  cancelScan: vi.fn(async () => undefined),
})

const moveJobs = reactive({
  trackedJobs: [] as Array<{
    jobId: string
    audiobookId?: number
    status: string
    progress: number
    phase?: string
    target?: string
  }>,
})

const library = reactive({ audiobooks: [] as Array<{ id: number; title: string }> })

vi.mock('@/stores/libraryImport', () => ({ useLibraryImportStore: () => libraryImport }))
vi.mock('@/stores/moveJobs', () => ({ useMoveJobsStore: () => moveJobs }))
vi.mock('@/stores/library', () => ({ useLibraryStore: () => library }))

import { useJobsStore } from '@/stores/jobs'

function scanTask(overrides: Partial<ScanTask> = {}): ScanTask {
  return {
    id: 'scan-a',
    jobId: 'unmatched-1',
    rootFolderId: 7,
    folderName: 'Audiobooks A',
    status: 'running',
    startedAt: '2026-08-08T10:40:00Z',
    finishedAt: null,
    error: null,
    itemCount: null,
    ...overrides,
  }
}

describe('jobs store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    libraryImport.scanTask = null
    libraryImport.scanHistory = []
    libraryImport.dismissScan.mockReset()
    libraryImport.clearFinishedScans.mockReset()
    libraryImport.cancelScan.mockReset()
    moveJobs.trackedJobs = []
    library.audiobooks = [{ id: 42, title: 'Dune' }]
  })

  describe('the Library Import scan', () => {
    it('is a running job that leads back to its folder, and can be canceled', () => {
      libraryImport.scanTask = scanTask({ status: 'starting' })

      const [job] = useJobsStore().jobs

      expect(job).toMatchObject({
        id: 'scan-a',
        kind: 'library-import-scan',
        title: 'Scanning Audiobooks A for unmatched files',
        status: 'running',
        active: true,
        cancelable: true,
        stale: false,
        startedAt: '2026-08-08T10:40:00Z',
        finishedAt: undefined,
        to: { name: 'library-import', query: { rootFolder: '7' } },
        action: {
          label: 'Follow job progress',
          to: { name: 'library-import', query: { rootFolder: '7' } },
          disabledReason: undefined,
        },
      })
    })

    it('offers its results once done, with when it finished', () => {
      libraryImport.scanTask = scanTask({
        status: 'done',
        itemCount: 3,
        finishedAt: '2026-08-08T10:42:00Z',
      })

      expect(useJobsStore().jobs[0]).toMatchObject({
        finishedAt: '2026-08-08T10:42:00Z',
        action: {
          label: 'See job results',
          to: { name: 'library-import', query: { rootFolder: '7' } },
          disabledReason: undefined,
        },
      })
    })

    it.each([
      ['failed', 'This scan has no results: it failed'],
      ['canceled', 'This scan has no results: it was canceled'],
    ] as const)('has no results to show when it %s', (status, reason) => {
      libraryImport.scanTask = scanTask({ status })

      expect(useJobsStore().jobs[0]?.action).toMatchObject({
        label: 'See job results',
        to: undefined,
        disabledReason: reason,
      })
    })

    it('lists every scan of the session, newest first', () => {
      libraryImport.scanTask = scanTask({ id: 'scan-c', rootFolderId: 9, folderName: 'C' })
      libraryImport.scanHistory = [
        scanTask({ id: 'scan-b', status: 'done', itemCount: 2 }),
        scanTask({ id: 'scan-a', status: 'canceled' }),
      ]

      expect(useJobsStore().jobs.map((job) => job.id)).toEqual(['scan-c', 'scan-b', 'scan-a'])
    })

    it('makes an older scan of a folder stale as soon as a newer one starts there', () => {
      libraryImport.scanTask = scanTask({ id: 'scan-new', status: 'starting' })
      libraryImport.scanHistory = [scanTask({ id: 'scan-old', status: 'done', itemCount: 2 })]

      const [newer, older] = useJobsStore().jobs

      expect(newer).toMatchObject({ stale: false, action: { label: 'Follow job progress' } })
      expect(older).toMatchObject({
        stale: true,
        to: undefined,
        action: {
          label: 'See job results',
          to: undefined,
          disabledReason:
            'Stale result: this scan was replaced by a more recent scan of the same folder',
        },
      })
    })

    it("keeps each folder's latest scan current", () => {
      libraryImport.scanTask = scanTask({ id: 'scan-b', rootFolderId: 9, folderName: 'B' })
      libraryImport.scanHistory = [scanTask({ id: 'scan-a', status: 'done', itemCount: 1 })]

      const older = useJobsStore().jobs[1]

      expect(older).toMatchObject({
        stale: false,
        to: { name: 'library-import', query: { rootFolder: '7' } },
        action: { label: 'See job results', disabledReason: undefined },
      })
    })

    it.each([
      [
        { status: 'done', itemCount: 14 } as const,
        'done',
        'Scan of Audiobooks A finished',
        '14 unmatched books',
      ],
      [
        { status: 'done', itemCount: 1 } as const,
        'done',
        'Scan of Audiobooks A finished',
        '1 unmatched book',
      ],
      [
        { status: 'failed', error: 'Root unavailable' } as const,
        'failed',
        'Scan of Audiobooks A failed',
        'Root unavailable',
      ],
      [
        { status: 'canceled' } as const,
        'canceled',
        'Scan of Audiobooks A canceled',
        'Library Import',
      ],
    ])('reports how it ended (%o)', (overrides, status, title, message) => {
      libraryImport.scanTask = scanTask(overrides)

      expect(useJobsStore().jobs[0]).toMatchObject({
        status,
        title,
        message,
        active: false,
        cancelable: false,
      })
    })

    it('names a folder it does not know "root folder"', () => {
      libraryImport.scanTask = scanTask({ folderName: null })

      expect(useJobsStore().jobs[0]?.title).toBe('Scanning root folder for unmatched files')
    })
  })

  it('lists a move with its book, phase, target and progress, linked to the book', () => {
    moveJobs.trackedJobs = [
      {
        jobId: 'm1',
        audiobookId: 42,
        status: 'Running',
        progress: 42.4,
        phase: 'Copying',
        target: '/new',
      },
      { jobId: 'm2', status: 'Queued', progress: 0 },
    ]

    const jobs = useJobsStore().jobs

    expect(jobs[0]).toMatchObject({
      id: 'move-m1',
      kind: 'move',
      title: 'Moving Dune',
      message: 'Copying to /new',
      status: 'running',
      progress: 42.4,
      active: true,
      to: { name: 'audiobook-detail', params: { id: '42' } },
    })
    expect(jobs[1]).toMatchObject({ title: 'Moving audiobook', status: 'queued', to: undefined })
  })

  it('lists the visible folder scans, linked to their book', () => {
    const scans = useScanNotificationsStore()
    scans.registerManualScan('s1', 42)
    scans.applyUpdate({ jobId: 'hidden', status: 'Processing' })

    expect(useJobsStore().jobs).toHaveLength(1)
    expect(useJobsStore().jobs[0]).toMatchObject({
      id: 'scan-s1',
      kind: 'folder-scan',
      title: 'Scan queued: Dune',
      status: 'queued',
      active: true,
      to: { name: 'audiobook-detail', params: { id: '42' } },
    })

    scans.applyUpdate({ jobId: 's1', status: 'Completed', found: 3, created: 2 })
    expect(useJobsStore().jobs[0]).toMatchObject({
      title: 'Scan complete: Dune',
      message: '3 files found · 2 added',
      status: 'done',
      active: false,
    })
  })

  it('lists deletes, with a percentage for a bulk delete only', () => {
    const deletes = useLibraryDeleteOperationsStore()
    const bulk = deletes.beginBulk(4)
    deletes.beginSingle(42, 'Dune')

    const jobs = useJobsStore().jobs
    const single = jobs.find((job) => job.title === 'Deleting Dune')
    const many = jobs.find((job) => job.id === bulk)

    expect(single).toMatchObject({ kind: 'delete', status: 'running', progress: undefined })
    expect(single?.to).toBeUndefined()
    expect(many).toMatchObject({ kind: 'delete', status: 'running', progress: 0 })
  })

  it('knows whether a job is running, for the spinning cog', () => {
    const store = useJobsStore()
    expect(store.hasActiveJobs).toBe(false)

    libraryImport.scanTask = scanTask({ status: 'done', itemCount: 0 })
    expect(store.hasActiveJobs).toBe(false)

    libraryImport.scanTask = scanTask()
    expect(store.hasActiveJobs).toBe(true)
  })

  it('dismisses each job through the store it comes from', () => {
    const scans = useScanNotificationsStore()
    scans.registerManualScan('s1', 42)
    scans.applyUpdate({ jobId: 's1', status: 'Completed' })
    const deletes = useLibraryDeleteOperationsStore()
    const deleteId = deletes.beginSingle(42, 'Dune')
    deletes.completeSingle(deleteId)
    libraryImport.scanTask = scanTask({ status: 'done', itemCount: 1 })
    libraryImport.scanHistory = [scanTask({ id: 'scan-old', status: 'canceled' })]
    const store = useJobsStore()

    store.dismiss('scan-old')
    store.dismiss('scan-s1')
    store.dismiss(deleteId)

    expect(libraryImport.dismissScan).toHaveBeenCalledWith('scan-old')
    expect(store.jobs.map((job) => job.id)).toEqual(['scan-a', 'scan-old'])
  })

  it('clears the jobs that ended and keeps the running ones', () => {
    const scans = useScanNotificationsStore()
    scans.registerManualScan('done', 42)
    scans.applyUpdate({ jobId: 'done', status: 'Completed' })
    scans.registerManualScan('running', 42)
    const store = useJobsStore()

    store.clearFinished()

    expect(libraryImport.clearFinishedScans).toHaveBeenCalled()
    expect(store.jobs.map((job) => job.id)).toEqual(['scan-running'])
  })

  it('cancels a running Library Import scan', async () => {
    libraryImport.scanTask = scanTask()

    await useJobsStore().cancel('scan-a')

    expect(libraryImport.cancelScan).toHaveBeenCalled()
  })

  it('records when folder scans and deletes finished', () => {
    const scans = useScanNotificationsStore()
    scans.registerManualScan('s1', 42)
    scans.applyUpdate({ jobId: 's1', status: 'Completed' })
    const deletes = useLibraryDeleteOperationsStore()
    deletes.completeSingle(deletes.beginSingle(42, 'Dune'))

    for (const job of useJobsStore().jobs) expect(job.finishedAt).toBeTruthy()
  })

  describe('description', () => {
    it('names the root dir a Library Import scan scans', () => {
      libraryImport.scanTask = scanTask({ folderPath: '/media/audiobooks' })
      libraryImport.scanHistory = [scanTask({ id: 'old', status: 'done', folderPath: undefined })]

      const [current, saved] = useJobsStore().jobs

      expect(current?.description).toBe('Scan root dir: /media/audiobooks')
      // Saved before scans kept their path: its name
      expect(saved?.description).toBe('Scan root dir: Audiobooks A')
    })

    it('describes the other jobs', () => {
      moveJobs.trackedJobs = [
        { jobId: 'm1', audiobookId: 42, status: 'Running', progress: 1, target: '/new' },
      ]
      useScanNotificationsStore().registerManualScan('s1', 42)
      const deletes = useLibraryDeleteOperationsStore()
      deletes.beginSingle(42, 'Dune')
      const bulk = deletes.beginBulk(3)

      const byId = (id: string) => useJobsStore().jobs.find((job) => job.id === id)?.description

      expect(byId('move-m1')).toBe('Move Dune to /new')
      expect(byId('scan-s1')).toBe('Scan book folder: Dune')
      expect(useJobsStore().jobs.find((job) => job.title === 'Deleting Dune')?.description).toBe(
        'Delete Dune',
      )
      expect(byId(bulk)).toBe('Delete 3 audiobooks')
    })
  })

  describe('job notifications, apart from the job log', () => {
    beforeEach(() => sessionStorage.clear())

    function endedAndRunning() {
      const scans = useScanNotificationsStore()
      scans.registerManualScan('ended', 42)
      scans.applyUpdate({ jobId: 'ended', status: 'Completed' })
      scans.registerManualScan('running', 42)
    }

    it('lists every job of the log until it is dismissed from the notifications', () => {
      endedAndRunning()
      const store = useJobsStore()

      expect(store.notificationJobs.map((job) => job.id)).toEqual(['scan-running', 'scan-ended'])

      store.dismissNotification('scan-ended')

      expect(store.notificationJobs.map((job) => job.id)).toEqual(['scan-running'])
      expect(store.jobs.map((job) => job.id)).toEqual(['scan-running', 'scan-ended'])
    })

    it('clears the notifications only, and shows a running job again when it ends', () => {
      endedAndRunning()
      libraryImport.scanTask = scanTask({ status: 'done', itemCount: 1 })
      const store = useJobsStore()

      store.clearNotifications()

      expect(store.notificationJobs).toEqual([])
      expect(store.jobs).toHaveLength(3)
      expect(libraryImport.clearFinishedScans).not.toHaveBeenCalled()

      useScanNotificationsStore().applyUpdate({ jobId: 'running', status: 'Completed' })
      expect(store.notificationJobs.map((job) => job.id)).toEqual(['scan-running'])
    })

    it('hides a running job, which goes on, until it ends', () => {
      endedAndRunning()
      const store = useJobsStore()

      store.dismissNotification('scan-running')
      expect(store.notificationJobs.map((job) => job.id)).toEqual(['scan-ended'])
      expect(store.jobs.find((job) => job.id === 'scan-running')?.active).toBe(true)

      useScanNotificationsStore().applyUpdate({ jobId: 'running', status: 'Processing' })
      expect(store.notificationJobs.map((job) => job.id)).toEqual(['scan-ended'])

      useScanNotificationsStore().applyUpdate({ jobId: 'running', status: 'Completed' })
      expect(store.notificationJobs.map((job) => job.id)).toEqual(['scan-running', 'scan-ended'])
    })

    it('remembers what was hidden after a reload', () => {
      endedAndRunning()
      useJobsStore().dismissNotification('scan-ended')

      setActivePinia(createPinia())
      endedAndRunning()

      expect(useJobsStore().notificationJobs.map((job) => job.id)).toEqual(['scan-running'])
    })

    it('drops from the notifications a job removed from the log', () => {
      endedAndRunning()
      const store = useJobsStore()

      store.dismiss('scan-ended')

      expect(store.notificationJobs.map((job) => job.id)).toEqual(['scan-running'])
    })
  })
})
