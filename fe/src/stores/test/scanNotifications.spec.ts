import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useScanNotificationsStore } from '@/stores/scanNotifications'

describe('scan notification store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('keeps internal scan updates hidden unless a manual scan is registered', () => {
    const store = useScanNotificationsStore()

    store.applyUpdate({ jobId: 'internal-1', audiobookId: 42, status: 'Processing' })
    store.applyUpdate({
      jobId: 'internal-1',
      audiobookId: 42,
      status: 'Completed',
      found: 2,
      created: 1,
    })

    expect(store.jobs).toHaveLength(1)
    expect(store.jobs[0]).toMatchObject({
      status: 'Completed',
      visible: false,
      found: 2,
      created: 1,
    })
  })

  it('reveals the latest state when a fast manual scan completes before registration', () => {
    const store = useScanNotificationsStore()

    store.applyUpdate({ jobId: 'manual-fast', audiobookId: 42, status: 'Processing' })
    store.applyUpdate({
      jobId: 'manual-fast',
      audiobookId: 42,
      status: 'Completed',
      found: 3,
      created: 2,
    })
    store.registerManualScan('manual-fast', 42)

    expect(store.jobs[0]).toMatchObject({
      jobId: 'manual-fast',
      audiobookId: 42,
      status: 'Completed',
      found: 3,
      created: 2,
      visible: true,
    })
  })

  it('does not regress terminal state when queued arrives after completion', () => {
    const store = useScanNotificationsStore()

    store.applyUpdate({
      jobId: 'manual-race',
      audiobookId: 42,
      status: 'Completed',
      found: 1,
      created: 1,
    })
    store.applyUpdate({ jobId: 'manual-race', audiobookId: 42, status: 'Queued' })

    expect(store.jobs[0]).toMatchObject({
      status: 'Completed',
      visible: true,
      found: 1,
      created: 1,
    })
  })

  it('preserves visible manual scans when hidden internal scan history is trimmed', () => {
    const store = useScanNotificationsStore()

    store.registerManualScan('manual-visible', 1)
    for (let index = 0; index < 60; index += 1) {
      store.applyUpdate({
        jobId: `internal-${index}`,
        audiobookId: index + 10,
        status: 'Processing',
      })
    }

    expect(store.jobs).toHaveLength(50)
    expect(store.jobs.some((job) => job.jobId === 'manual-visible' && job.visible)).toBe(true)
  })

  it('never evicts active visible manual scans just to enforce the history cap', () => {
    const store = useScanNotificationsStore()

    for (let index = 0; index < 55; index += 1) {
      store.registerManualScan(`manual-${index}`, index + 1)
    }

    expect(store.jobs).toHaveLength(55)
    expect(store.jobs.every((job) => job.visible && job.status === 'Queued')).toBe(true)
  })

  it('does not let a conflicting late terminal state overwrite completion', () => {
    const store = useScanNotificationsStore()

    store.registerManualScan('scan-terminal', 42)
    store.applyUpdate({
      jobId: 'scan-terminal',
      audiobookId: 42,
      status: 'Completed',
      found: 3,
      created: 2,
    })
    store.applyUpdate({
      jobId: 'scan-terminal',
      audiobookId: 42,
      status: 'Failed',
      error: 'Scan status is no longer available',
    })

    expect(store.jobs[0]).toMatchObject({
      status: 'Completed',
      found: 3,
      created: 2,
    })
    expect(store.jobs[0]?.error).toBeUndefined()
  })

  it('keeps active scans through clear and makes terminal scans dismissible', () => {
    const store = useScanNotificationsStore()

    store.registerManualScan('active', 1)
    store.registerManualScan('finished', 2)
    store.applyUpdate({ jobId: 'finished', audiobookId: 2, status: 'Completed' })

    store.dismiss('active')
    expect(store.jobs.find((job) => job.jobId === 'active')?.dismissed).toBe(false)

    store.dismiss('finished')
    expect(store.jobs.find((job) => job.jobId === 'finished')?.dismissed).toBe(true)

    store.clearFinished()
    expect(store.jobs.map((job) => job.jobId)).toEqual(['active'])
  })
})
