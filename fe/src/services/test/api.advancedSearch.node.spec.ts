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

describe('ApiService advancedSearch', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('omits author when asin is provided', async () => {
    vi.resetModules()

    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify({ results: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    await actual.apiService.advancedSearch({
      asin: 'B0DQR9D4YG',
      author: 'SenLinYu',
      cap: 5,
    })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [, options] = fetchMock.mock.calls[0] as any as [RequestInfo, RequestInit]
    const body = JSON.parse(String(options.body))

    expect(body).toEqual({
      mode: 'Advanced',
      asin: 'B0DQR9D4YG',
      cap: 5,
    })
    expect(body.author).toBeUndefined()
  })

  it('includes author when searching without asin', async () => {
    vi.resetModules()

    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify({ results: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    await actual.apiService.advancedSearch({
      title: 'Alchemised',
      author: 'SenLinYu',
      cap: 5,
    })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [, options] = fetchMock.mock.calls[0] as any as [RequestInfo, RequestInit]
    const body = JSON.parse(String(options.body))

    expect(body).toEqual({
      mode: 'Advanced',
      title: 'Alchemised',
      author: 'SenLinYu',
      cap: 5,
    })
  })
})
