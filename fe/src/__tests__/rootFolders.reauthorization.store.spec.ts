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

describe('root folder storage and relocation store actions', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setActivePinia(createPinia())
  })

  it('does not let an older load overwrite a newer root-folder snapshot', async () => {
    const older = {
      id: 3,
      name: 'Library',
      path: '/srv/Old',
      isDefault: false,
      caseSensitivityMode: 'Auto' as const,
    }
    const newer = { ...older, path: '/srv/New' }
    let resolveOlder: ((folders: (typeof older)[]) => void) | undefined
    vi.mocked(apiService.getRootFolders)
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveOlder = resolve
          }),
      )
      .mockResolvedValueOnce([newer])
    const store = useRootFoldersStore()

    const olderLoad = store.load()
    await store.load()
    resolveOlder?.([older])
    await olderLoad

    expect(store.folders).toEqual([newer])
    expect(store.loading).toBe(false)
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

  it('routes case-sensitivity changes through metadata-only path migration', async () => {
    const current = {
      id: 3,
      name: 'Library',
      path: '/srv/Library',
      isDefault: false,
      caseSensitivityMode: 'Sensitive' as const,
    }
    const updated = {
      ...current,
      name: 'Renamed',
      caseSensitivityMode: 'Insensitive' as const,
    }
    vi.mocked(apiService.changeRootFolderPath).mockResolvedValueOnce({
      relocationId: null,
      rootFolderId: 3,
      currentPath: current.path,
      targetPath: current.path,
      status: 'Completed',
      totalJobs: 0,
      completedJobs: 0,
      targetIdentityEnrollmentState: 'Authorized',
    })
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([updated])
    const store = useRootFoldersStore()
    store.folders = [current]

    await store.update(3, updated)

    expect(apiService.updateRootFolder).not.toHaveBeenCalled()
    expect(apiService.changeRootFolderPath).toHaveBeenCalledWith(3, {
      targetPath: current.path,
      mode: 'metadataOnly',
      deleteEmptySource: false,
      desiredName: updated.name,
      desiredIsDefault: false,
      targetCaseSensitivityMode: 'Insensitive',
      expectedCurrentPath: current.path,
    })
    expect(apiService.getRootFolders).toHaveBeenCalledTimes(1)
  })

  it('routes same-path storage-semantics repair through confirmed metadata-only migration', async () => {
    const current = {
      id: 3,
      name: 'Library',
      path: '/srv/Library',
      isDefault: false,
      caseSensitivityMode: 'Auto' as const,
      storageState: 'Unavailable' as const,
      storageReason: 'FilesystemSemanticsChanged' as const,
    }
    vi.mocked(apiService.changeRootFolderPath).mockResolvedValueOnce({
      relocationId: null,
      rootFolderId: 3,
      currentPath: current.path,
      targetPath: current.path,
      status: 'Completed',
      totalJobs: 0,
      completedJobs: 0,
      targetIdentityEnrollmentState: 'Authorized',
    })
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([
      {
        ...current,
        storageState: 'Healthy' as const,
        storageReason: 'None' as const,
      },
    ])
    const store = useRootFoldersStore()
    store.folders = [current]

    await store.update(3, current, {
      expectedCurrentPath: current.path,
      pathChangeConfirmed: true,
      moveFiles: false,
      deleteEmptySource: false,
    })

    expect(apiService.updateRootFolder).not.toHaveBeenCalled()
    expect(apiService.changeRootFolderPath).toHaveBeenCalledWith(3, {
      targetPath: current.path,
      mode: 'metadataOnly',
      deleteEmptySource: false,
      desiredName: current.name,
      desiredIsDefault: false,
      targetCaseSensitivityMode: 'Auto',
      expectedCurrentPath: current.path,
    })
  })

  it('returns metadata-only attention as a successful root repair', async () => {
    const current = {
      id: 3,
      name: 'Library',
      path: '/srv/Library',
      isDefault: false,
      caseSensitivityMode: 'Sensitive' as const,
    }
    const updated = {
      ...current,
      caseSensitivityMode: 'Insensitive' as const,
    }
    vi.mocked(apiService.changeRootFolderPath).mockResolvedValueOnce({
      relocationId: 'relocation-semantics',
      rootFolderId: 3,
      currentPath: current.path,
      targetPath: current.path,
      status: 'NeedsAttention',
      totalJobs: 0,
      completedJobs: 0,
      error:
        'The relocation requires attention. Review the affected move jobs and retry after resolving the underlying issue.',
      targetIdentityEnrollmentState: 'Authorized',
    })
    const updatedWithAttention = {
      ...updated,
      activeRelocation: {
        relocationId: 'relocation-semantics',
        rootFolderId: 3,
        currentPath: current.path,
        targetPath: current.path,
        status: 'NeedsAttention' as const,
        totalJobs: 1,
        completedJobs: 0,
        error: 'The relocation requires attention.',
        targetIdentityEnrollmentState: 'Authorized' as const,
      },
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([updatedWithAttention])
    const store = useRootFoldersStore()
    store.folders = [current]

    await expect(store.update(3, updated)).resolves.toEqual(updatedWithAttention)

    expect(apiService.updateRootFolder).not.toHaveBeenCalled()
    expect(apiService.getRootFolders).toHaveBeenCalledTimes(1)
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

  it('passes the exact path and observation token when confirming a root folder', async () => {
    const current = {
      id: 3,
      name: 'Library',
      path: '/srv/Library ',
      isDefault: false,
      caseSensitivityMode: 'Auto' as const,
      storageState: 'Unconfirmed' as const,
      confirmationToken: 'observation-token',
    }
    vi.mocked(apiService.confirmRootFolder).mockResolvedValueOnce(current)
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([current])
    const store = useRootFoldersStore()

    await expect(
      store.confirmCurrentFolder(current.id, current.path, current.confirmationToken),
    ).resolves.toEqual(current)

    expect(apiService.confirmRootFolder).toHaveBeenCalledWith(
      current.id,
      current.path,
      current.confirmationToken,
    )
    expect(apiService.getRootFolders).toHaveBeenCalledTimes(1)
  })
})
