/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { afterEach, describe, expect, it, vi } from 'vitest'

describe('ApiService root-folder reauthorization', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('posts the exact confirmed root path to the physical identity endpoint', async () => {
    vi.resetModules()
    const rootPath = '/srv/Library '
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            id: 3,
            name: 'Library',
            path: rootPath,
            isDefault: true,
          }),
          {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          },
        ),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    await actual.apiService.reauthorizeRootFolderIdentity(3, rootPath)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [requestInfo, options] = fetchMock.mock.calls[0] as [RequestInfo, RequestInit]
    expect(String(requestInfo)).toContain('/rootfolders/3/reauthorize-identity')
    expect(options.method).toBe('POST')
    expect(JSON.parse(String(options.body))).toEqual({ expectedCurrentPath: rootPath })
  })

  it('posts the exact confirmed target path to the dedicated endpoint', async () => {
    vi.resetModules()
    const targetPath = '/srv/Audiobooks '
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            relocationId: 'relocation-1',
            rootFolderId: 3,
            currentPath: '/srv/Old',
            targetPath,
            status: 'Running',
            totalJobs: 1,
            completedJobs: 0,
            targetIdentityEnrollmentState: 'Authorized',
          }),
          {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          },
        ),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    await actual.apiService.reauthorizeLegacyRootFolderRelocationTarget('relocation-1', targetPath)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [requestInfo, options] = fetchMock.mock.calls[0] as [RequestInfo, RequestInit]
    expect(String(requestInfo)).toContain(
      '/rootfolder-relocations/relocation-1/reauthorize-legacy-target',
    )
    expect(options.method).toBe('POST')
    expect(JSON.parse(String(options.body))).toEqual({ confirmedTargetPath: targetPath })
  })
})
