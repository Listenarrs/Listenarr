/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { executeBulkEdit } from '@/utils/bulkEditOrchestration'

function createDependencies() {
  return {
    bulkUpdateAudiobooks: vi.fn(async (ids: number[]) => ({
      message: 'updated',
      results: ids.map((id) => ({
        id,
        success: true,
        metadataUpdated: true,
        pathChangeOutcome: 'enqueued',
        moveJobId: `job-${id}`,
        resolvedDestination: `/library-new/Book ${id}`,
        errors: [] as string[],
      })),
    })),
    trackQueuedJob: vi.fn(),
  }
}

describe('bulk edit orchestration', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('sends one backend-owned physical path-change request and registers returned jobs', async () => {
    const dependencies = createDependencies()
    const destinationRoot = '/library-new '
    dependencies.bulkUpdateAudiobooks.mockResolvedValueOnce({
      message: 'updated',
      results: [
        {
          id: 1,
          success: true,
          metadataUpdated: true,
          pathChangeOutcome: 'enqueued',
          moveJobId: 'job-1',
          resolvedDestination: '/library-new /Book 1 ',
          errors: [],
        },
        {
          id: 2,
          success: true,
          metadataUpdated: true,
          pathChangeOutcome: 'enqueued',
          moveJobId: 'job-2',
          resolvedDestination: '/library-new /Book 2 ',
          errors: [],
        },
      ],
    })

    const outcome = await executeBulkEdit(
      {
        ids: [1, 2],
        updates: {
          monitored: true,
          rootFolder: destinationRoot,
          moveFiles: true,
          deleteEmptySource: true,
        },
        destinationRoot,
        moveFiles: true,
        deleteEmptySource: true,
      },
      dependencies,
    )

    expect(dependencies.bulkUpdateAudiobooks).toHaveBeenCalledTimes(1)
    expect(dependencies.bulkUpdateAudiobooks).toHaveBeenCalledWith(
      [1, 2],
      { monitored: true },
      {
        mode: 'Physical',
        destinationRootOrPath: destinationRoot,
        deleteEmptySource: true,
      },
    )
    expect(dependencies.trackQueuedJob).toHaveBeenNthCalledWith(1, {
      jobId: 'job-1',
      audiobookId: 1,
      target: '/library-new /Book 1 ',
    })
    expect(dependencies.trackQueuedJob).toHaveBeenNthCalledWith(2, {
      jobId: 'job-2',
      audiobookId: 2,
      target: '/library-new /Book 2 ',
    })
    expect(outcome.results.every((result) => result.success)).toBe(true)
  })

  it.each([
    ['missing', undefined, 'The server did not return a path-change outcome.'],
    ['unknown', 'queued-sometime', 'The server returned an unrecognized path-change outcome.'],
  ])('fails closed for a %s physical path-change outcome', async (_, pathChangeOutcome, error) => {
    const dependencies = createDependencies()
    dependencies.bulkUpdateAudiobooks.mockResolvedValueOnce({
      message: 'updated',
      results: [
        {
          id: 1,
          success: true,
          metadataUpdated: true,
          pathChangeOutcome,
          moveJobId: 'job-1',
          resolvedDestination: '/library-new/Book 1',
          errors: [],
        },
      ],
    })

    const outcome = await executeBulkEdit(
      {
        ids: [1],
        updates: {},
        destinationRoot: '/library-new',
        moveFiles: true,
        deleteEmptySource: false,
      },
      dependencies,
    )

    expect(outcome.results[0]).toEqual(
      expect.objectContaining({
        id: 1,
        success: false,
        errors: expect.arrayContaining([error]),
      }),
    )
    expect(dependencies.trackQueuedJob).not.toHaveBeenCalled()
  })

  it('fails closed when a valid outcome does not match the requested operation', async () => {
    const dependencies = createDependencies()
    dependencies.bulkUpdateAudiobooks.mockResolvedValueOnce({
      message: 'updated',
      results: [
        {
          id: 1,
          success: true,
          metadataUpdated: true,
          pathChangeOutcome: 'metadata-updated',
          moveJobId: 'job-1',
          resolvedDestination: '/library-new/Book 1',
          errors: [],
        },
      ],
    })

    const outcome = await executeBulkEdit(
      {
        ids: [1],
        updates: {},
        destinationRoot: '/library-new',
        moveFiles: true,
        deleteEmptySource: false,
      },
      dependencies,
    )

    expect(outcome.results[0]).toEqual(
      expect.objectContaining({
        id: 1,
        success: false,
        errors: expect.arrayContaining([
          'The server returned a path-change outcome that does not match the request.',
        ]),
      }),
    )
    expect(dependencies.trackQueuedJob).not.toHaveBeenCalled()
  })

  it('surfaces backend per-item move failures without registering failed jobs', async () => {
    const dependencies = createDependencies()
    dependencies.bulkUpdateAudiobooks.mockResolvedValueOnce({
      message: 'updated',
      results: [
        {
          id: 1,
          success: true,
          metadataUpdated: true,
          pathChangeOutcome: 'enqueued',
          moveJobId: 'job-1',
          resolvedDestination: '/library-new/Book 1',
          errors: [],
        },
        {
          id: 2,
          success: false,
          metadataUpdated: true,
          pathChangeOutcome: 'failed',
          moveJobId: null,
          resolvedDestination: '/library-new/Book 2',
          errors: ['queue unavailable'],
        },
      ],
    })

    const outcome = await executeBulkEdit(
      {
        ids: [1, 2],
        updates: { rootFolder: '/library-new', moveFiles: true },
        destinationRoot: '/library-new',
        moveFiles: true,
        deleteEmptySource: false,
      },
      dependencies,
    )

    expect(outcome.results[1]).toEqual(
      expect.objectContaining({ id: 2, success: false, errors: ['queue unavailable'] }),
    )
    expect(dependencies.trackQueuedJob).toHaveBeenCalledTimes(1)
    expect(dependencies.trackQueuedJob).not.toHaveBeenCalledWith(
      expect.objectContaining({ audiobookId: 2 }),
    )
  })

  it('fails closed when a successful physical result omits its durable job id', async () => {
    const dependencies = createDependencies()
    dependencies.bulkUpdateAudiobooks.mockResolvedValueOnce({
      message: 'updated',
      results: [
        {
          id: 1,
          success: true,
          metadataUpdated: true,
          pathChangeOutcome: 'enqueued',
          moveJobId: null,
          resolvedDestination: '/library-new/Book 1',
          errors: [],
        },
      ],
    })

    const outcome = await executeBulkEdit(
      {
        ids: [1],
        updates: {},
        destinationRoot: '/library-new',
        moveFiles: true,
        deleteEmptySource: false,
      },
      dependencies,
    )

    expect(outcome.results[0]).toEqual(
      expect.objectContaining({
        id: 1,
        success: false,
        errors: ['The server did not return a durable move job ID.'],
      }),
    )
    expect(dependencies.trackQueuedJob).not.toHaveBeenCalled()
  })

  it('uses typed metadata-only path changes and does not register move jobs', async () => {
    const dependencies = createDependencies()
    dependencies.bulkUpdateAudiobooks.mockResolvedValueOnce({
      message: 'updated',
      results: [
        {
          id: 1,
          success: true,
          metadataUpdated: true,
          pathChangeOutcome: 'metadata-updated',
          moveJobId: null,
          resolvedDestination: '/library-new/Book 1',
          errors: [],
        },
      ],
    })

    await executeBulkEdit(
      {
        ids: [1],
        updates: { monitored: false, rootFolder: '/library-new' },
        destinationRoot: '/library-new',
        moveFiles: false,
        deleteEmptySource: false,
      },
      dependencies,
    )

    expect(dependencies.bulkUpdateAudiobooks).toHaveBeenCalledWith(
      [1],
      { monitored: false },
      {
        mode: 'MetadataOnly',
        destinationRootOrPath: '/library-new',
        deleteEmptySource: false,
      },
    )
    expect(dependencies.trackQueuedJob).not.toHaveBeenCalled()
  })
})
