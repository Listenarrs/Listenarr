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

describe('ApiService removeFromLibrary', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('sends deleteFiles and deleteFolder query params when requested', async () => {
    vi.resetModules()

    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify({ message: 'deleted', id: 42 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    await actual.apiService.removeFromLibrary(42, { deleteFiles: true, deleteFolder: true })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [requestInfo, options] = fetchMock.mock.calls[0] as any as [RequestInfo, RequestInit]
    expect(String(requestInfo)).toContain('/library/42?deleteFiles=true&deleteFolder=true')
    expect(options.method).toBe('DELETE')
  })
})
