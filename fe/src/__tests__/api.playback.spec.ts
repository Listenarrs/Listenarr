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
import { describe, it, expect, vi, afterEach } from 'vitest'

describe('ApiService playback', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('streamUrl returns a string containing the expected path', async () => {
    vi.resetModules()

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    const url = actual.apiService.streamUrl(1, 0)

    expect(url).toContain('/audiobooks/1/files/0/stream')
  })

  it('getPlayback issues a GET to the playback endpoint and returns parsed JSON', async () => {
    vi.resetModules()

    const mockState = {
      audiobookId: 7,
      title: 'Test Book',
      asin: null,
      files: [],
      fileIndex: 0,
      positionSeconds: 0,
      finished: false,
    }
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(mockState), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    const result = await actual.apiService.getPlayback(7)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [requestInfo, options] = fetchMock.mock.calls[0] as [RequestInfo, RequestInit]
    expect(String(requestInfo)).toContain('/audiobooks/7/playback')
    expect((options.method || 'GET').toUpperCase()).toBe('GET')
    expect(result).toEqual(mockState)
  })

  it('savePlayback issues a PUT to the playback endpoint with the JSON body', async () => {
    vi.resetModules()

    const fetchMock = vi.fn(() =>
      Promise.resolve(new Response(null, { status: 204 })),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    await actual.apiService.savePlayback(7, { fileIndex: 0, positionSeconds: 12.5, finished: false })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [requestInfo, options] = fetchMock.mock.calls[0] as [RequestInfo, RequestInit]
    expect(String(requestInfo)).toContain('/audiobooks/7/playback')
    expect(options.method).toBe('PUT')
    const body = JSON.parse(String(options.body))
    expect(body).toEqual({ fileIndex: 0, positionSeconds: 12.5, finished: false })
  })
})
