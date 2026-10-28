import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import type { Audiobook } from '@/types'
import { apiService } from '@/services/api'
import { useLibraryStore } from '@/stores/library'
import { useLibraryDeleteOperationsStore } from '@/stores/libraryDeleteOperations'

vi.mock('@/services/signalr', () => ({
  signalRService: {
    onFilesRemoved: vi.fn(() => () => undefined),
    onAudiobookUpdate: vi.fn(() => () => undefined),
  },
}))

vi.mock('@/services/api', () => ({
  apiService: {
    removeFromLibrary: vi.fn(),
  },
}))

const removeFromLibraryMock = vi.mocked(apiService.removeFromLibrary)

function audiobook(id: number, title: string): Audiobook {
  return { id, title } as Audiobook
}

describe('library delete notification operations', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    removeFromLibraryMock.mockReset()
  })

  it('tracks an individual deletion while the request is in flight', async () => {
    let completeRequest!: (value: { message: string; id: number }) => void
    removeFromLibraryMock.mockImplementation(
      () =>
        new Promise((resolve) => {
          completeRequest = resolve
        }),
    )
    const libraryStore = useLibraryStore()
    const operationsStore = useLibraryDeleteOperationsStore()
    libraryStore.audiobooks = [audiobook(42, 'Slow Delete')]

    const deletion = libraryStore.removeFromLibrary(42)

    expect(operationsStore.operations).toHaveLength(1)
    expect(operationsStore.operations[0]).toMatchObject({
      kind: 'single',
      title: 'Slow Delete',
      audiobookId: 42,
      status: 'deleting',
      progress: 35,
    })

    completeRequest({ message: 'deleted', id: 42 })
    await expect(deletion).resolves.toBe(true)

    expect(operationsStore.operations[0]).toMatchObject({
      status: 'completed',
      progress: 100,
      processed: 1,
      deleted: 1,
    })
    expect(libraryStore.audiobooks).toEqual([])
  })

  it('retries a blocked physical delete inside the same notification operation', async () => {
    const blocked = new Error('Filesystem mutation unavailable')
    removeFromLibraryMock
      .mockRejectedValueOnce(blocked)
      .mockResolvedValueOnce({ message: 'deleted', id: 42 })
    const retryAfterBlockedMutation = vi.fn().mockResolvedValue(true)
    const libraryStore = useLibraryStore()
    const operationsStore = useLibraryDeleteOperationsStore()
    libraryStore.audiobooks = [audiobook(42, 'Network Delete')]

    await expect(
      libraryStore.removeFromLibrary(42, {
        deleteFiles: true,
        deleteFolder: true,
        retryAfterBlockedMutation,
      }),
    ).resolves.toBe(true)

    expect(retryAfterBlockedMutation).toHaveBeenCalledWith(blocked)
    expect(removeFromLibraryMock).toHaveBeenCalledTimes(2)
    expect(operationsStore.operations).toHaveLength(1)
    expect(operationsStore.operations[0]).toMatchObject({
      kind: 'single',
      status: 'completed',
      deleted: 1,
      failed: 0,
    })
    expect(libraryStore.audiobooks).toEqual([])
  })

  it('removes the in-flight delete notification when storage confirmation is cancelled', async () => {
    const blocked = new Error('Filesystem mutation unavailable')
    removeFromLibraryMock.mockRejectedValueOnce(blocked)
    const retryAfterBlockedMutation = vi.fn().mockResolvedValue('cancel' as const)
    const libraryStore = useLibraryStore()
    const operationsStore = useLibraryDeleteOperationsStore()
    libraryStore.audiobooks = [audiobook(42, 'Cancelled Delete')]

    await expect(
      libraryStore.removeFromLibrary(42, {
        deleteFiles: true,
        retryAfterBlockedMutation,
      }),
    ).resolves.toBeNull()

    expect(retryAfterBlockedMutation).toHaveBeenCalledWith(blocked)
    expect(removeFromLibraryMock).toHaveBeenCalledTimes(1)
    expect(operationsStore.operations).toHaveLength(0)
    expect(libraryStore.audiobooks.map((book) => book.id)).toEqual([42])
  })

  it('keeps an interrupted bulk operation failed until every item is processed', () => {
    const operationsStore = useLibraryDeleteOperationsStore()
    const operationId = operationsStore.beginBulk(3)

    operationsStore.setBulkCurrentItem(operationId, 'First')
    operationsStore.updateBulkItem(operationId, 'First', true)
    operationsStore.setBulkCurrentItem(operationId, 'Second')
    operationsStore.finishBulk(operationId)

    expect(operationsStore.operations[0]).toMatchObject({
      status: 'failed',
      total: 3,
      processed: 1,
      deleted: 1,
      failed: 0,
      currentTitle: 'Second',
    })
    expect(operationsStore.operations[0]?.progress).toBeCloseTo(100 / 3)
  })

  it('keeps active deletes visible while allowing finished notifications to be dismissed or cleared', () => {
    const operationsStore = useLibraryDeleteOperationsStore()
    const activeId = operationsStore.beginSingle(1, 'Active')
    const completedId = operationsStore.beginSingle(2, 'Completed')
    operationsStore.completeSingle(completedId)
    const failedId = operationsStore.beginSingle(3, 'Failed')
    operationsStore.failSingle(failedId, 'Blocked')

    operationsStore.dismiss(activeId)
    operationsStore.dismiss(completedId)

    expect(
      operationsStore.operations.find((operation) => operation.id === activeId)?.dismissed,
    ).toBe(false)
    expect(
      operationsStore.operations.find((operation) => operation.id === completedId)?.dismissed,
    ).toBe(true)

    operationsStore.clearFinished()

    expect(operationsStore.operations).toHaveLength(1)
    expect(operationsStore.operations[0]?.id).toBe(activeId)
  })

  it('never evicts active delete notifications just to enforce the history cap', () => {
    const operationsStore = useLibraryDeleteOperationsStore()

    for (let index = 0; index < 55; index += 1) {
      operationsStore.beginSingle(index + 1, `Active ${index + 1}`)
    }

    expect(operationsStore.operations).toHaveLength(55)
    expect(operationsStore.operations.every((operation) => operation.status === 'deleting')).toBe(
      true,
    )
  })

  it('tracks real aggregate bulk progress and preserves rows whose deletion failed', async () => {
    removeFromLibraryMock
      .mockResolvedValueOnce({ message: 'deleted', id: 1 })
      .mockRejectedValueOnce(new Error('Delete blocked'))
      .mockResolvedValueOnce({ message: 'deleted', id: 3 })
    const libraryStore = useLibraryStore()
    const operationsStore = useLibraryDeleteOperationsStore()
    libraryStore.audiobooks = [audiobook(1, 'First'), audiobook(2, 'Second'), audiobook(3, 'Third')]

    const result = await libraryStore.bulkRemoveFromLibrary([1, 2, 3])

    expect(result).toEqual({ success: true, deletedCount: 2 })
    expect(operationsStore.operations).toHaveLength(1)
    expect(operationsStore.operations[0]).toMatchObject({
      kind: 'bulk',
      status: 'failed',
      progress: 100,
      total: 3,
      processed: 3,
      deleted: 2,
      failed: 1,
      currentTitle: 'Third',
      error: 'Delete blocked',
    })
    expect(libraryStore.audiobooks.map((book) => book.id)).toEqual([2])
  })
})
