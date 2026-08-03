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
import { createPinia, setActivePinia } from 'pinia'
import { apiService } from '@/services/api'
import { useRootFoldersStore } from '@/stores/rootFolders'
import type { RootFolderPathChangeResult } from '@/types'

describe('root folder relocation store actions', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setActivePinia(createPinia())
  })

  it('sends the exact current path as the server relocation precondition', async () => {
    const current = {
      id: 3,
      name: 'Library',
      path: '/srv/Old',
      isDefault: false,
      caseSensitivityMode: 'Auto' as const,
    }
    const updated = { ...current, path: '/srv/New' }
    vi.mocked(apiService.changeRootFolderPath).mockResolvedValueOnce({
      relocationId: 'relocation-1',
      rootFolderId: 3,
      currentPath: current.path,
      targetPath: updated.path,
      status: 'Pending',
      totalJobs: 1,
      completedJobs: 0,
      targetIdentityEnrollmentState: 'Authorized',
    })
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([updated])
    const store = useRootFoldersStore()
    store.folders = [current]

    await store.update(3, updated, {
      expectedCurrentPath: current.path,
      pathChangeConfirmed: true,
      moveFiles: true,
      deleteEmptySource: true,
    })

    expect(apiService.changeRootFolderPath).toHaveBeenCalledWith(
      3,
      expect.objectContaining({
        targetPath: updated.path,
        expectedCurrentPath: current.path,
      }),
    )
  })

  it('surfaces a synchronous relocation attention result instead of reporting success', async () => {
    const current = {
      id: 3,
      name: 'Library',
      path: '/srv/Old',
      isDefault: false,
      caseSensitivityMode: 'Auto' as const,
    }
    const updated = { ...current, path: '/srv/New' }
    vi.mocked(apiService.changeRootFolderPath).mockResolvedValueOnce({
      relocationId: 'relocation-1',
      rootFolderId: 3,
      currentPath: current.path,
      targetPath: updated.path,
      status: 'NeedsAttention',
      totalJobs: 1,
      completedJobs: 0,
      error:
        'The relocation requires attention. Review the affected move jobs and retry after resolving the underlying issue.',
      targetIdentityEnrollmentState: 'Authorized',
    })
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([current])
    const store = useRootFoldersStore()
    store.folders = [current]

    await expect(
      store.update(3, updated, {
        expectedCurrentPath: current.path,
        pathChangeConfirmed: true,
        moveFiles: true,
        deleteEmptySource: true,
      }),
    ).rejects.toThrow('relocation requires attention')

    expect(apiService.getRootFolders).toHaveBeenCalledTimes(1)
  })

  it('passes the exact confirmed target path and reloads root folders', async () => {
    const targetPath = '/srv/Audiobooks '
    const result: RootFolderPathChangeResult = {
      relocationId: 'relocation-1',
      rootFolderId: 3,
      currentPath: '/srv/Old',
      targetPath,
      status: 'Running',
      totalJobs: 1,
      completedJobs: 0,
      targetIdentityEnrollmentState: 'Authorized',
    }
    vi.mocked(apiService.reauthorizeLegacyRootFolderRelocationTarget).mockResolvedValueOnce(result)
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([])
    const store = useRootFoldersStore()

    await expect(store.reauthorizeLegacyTarget('relocation-1', targetPath)).resolves.toEqual(result)

    expect(apiService.reauthorizeLegacyRootFolderRelocationTarget).toHaveBeenCalledWith(
      'relocation-1',
      targetPath,
    )
    expect(apiService.getRootFolders).toHaveBeenCalledTimes(1)
  })
})
