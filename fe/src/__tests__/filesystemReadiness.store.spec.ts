/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

const apiMocks = vi.hoisted(() => ({
  getSystemReadiness: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getSystemReadiness: apiMocks.getSystemReadiness,
  },
}))

import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'

function readiness(filesystemStatus: 'Pending' | 'Running' | 'Ready' | 'Failed') {
  return {
    isReady: true,
    status: 'ready',
    databaseConnected: true,
    migrationsCurrent: true,
    filesystemReady: filesystemStatus === 'Ready',
    filesystemStatus,
    filesystemPhase: filesystemStatus === 'Running' ? 'AudiobookFileIdentities' : null,
    filesystemErrorCode: filesystemStatus === 'Failed' ? 'filesystem_initialization_failed' : null,
    filesystemErrorMessage:
      filesystemStatus === 'Failed' ? 'Injected filesystem initialization failure.' : null,
  }
}

describe('filesystem readiness store', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.clearAllMocks()
    setActivePinia(createPinia())
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('polls while reconciliation is running and stops after Ready', async () => {
    apiMocks.getSystemReadiness
      .mockResolvedValueOnce(readiness('Running'))
      .mockResolvedValueOnce(readiness('Ready'))
    const store = useFilesystemReadinessStore()

    store.start()
    await vi.waitFor(() => expect(store.filesystemStatus).toBe('Running'))

    expect(store.filesystemReady).toBe(false)
    expect(store.filesystemInitializing).toBe(true)
    expect(apiMocks.getSystemReadiness).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(1500)
    await vi.waitFor(() => expect(store.filesystemStatus).toBe('Ready'))

    expect(store.filesystemReady).toBe(true)
    expect(store.filesystemInitializing).toBe(false)
    expect(apiMocks.getSystemReadiness).toHaveBeenCalledTimes(2)

    await vi.advanceTimersByTimeAsync(5000)
    expect(apiMocks.getSystemReadiness).toHaveBeenCalledTimes(2)
  })

  it('stops polling and exposes failure details after Failed', async () => {
    apiMocks.getSystemReadiness.mockResolvedValue(readiness('Failed'))
    const store = useFilesystemReadinessStore()

    store.start()
    await vi.waitFor(() => expect(store.filesystemStatus).toBe('Failed'))

    expect(store.filesystemReady).toBe(false)
    expect(store.filesystemFailed).toBe(true)
    expect(store.readiness?.filesystemErrorCode).toBe('filesystem_initialization_failed')

    await vi.advanceTimersByTimeAsync(5000)
    expect(apiMocks.getSystemReadiness).toHaveBeenCalledTimes(1)
  })

  it('stop prevents a queued poll from running', async () => {
    apiMocks.getSystemReadiness.mockResolvedValue(readiness('Running'))
    const store = useFilesystemReadinessStore()

    store.start()
    await vi.waitFor(() => expect(apiMocks.getSystemReadiness).toHaveBeenCalledTimes(1))
    store.stop()

    await vi.advanceTimersByTimeAsync(5000)
    expect(apiMocks.getSystemReadiness).toHaveBeenCalledTimes(1)
  })
})
