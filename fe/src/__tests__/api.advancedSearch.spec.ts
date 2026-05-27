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
    const [, options] = fetchMock.mock.calls[0] as [RequestInfo, RequestInit]
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
    const [, options] = fetchMock.mock.calls[0] as [RequestInfo, RequestInit]
    const body = JSON.parse(String(options.body))

    expect(body).toEqual({
      mode: 'Advanced',
      title: 'Alchemised',
      author: 'SenLinYu',
      cap: 5,
    })
  })

  it('searchAudibleByTitleAndAuthor normalizes a bare-array response into the envelope shape', async () => {
    // The backend POST /search endpoint returns a flat array of Audible-shaped
    // results (Ok(flatMapped) in SearchController.cs). Earlier this method
    // typed the response as `{ results: [] }` and silently dropped real hits
    // when the array shape arrived. Make sure both shapes are handled.
    vi.resetModules()

    const flatArray = [
      { asin: 'B009KS9JKA', title: 'A Darkness upon the Ice', authors: [{ name: 'W. Forstchen' }] },
    ]
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(flatArray), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    const response = await actual.apiService.searchAudibleByTitleAndAuthor(
      'A Darkness Upon the Ice',
      'William R. Forstchen',
    )

    expect(response.totalResults).toBe(1)
    expect(response.results).toHaveLength(1)
    expect(response.results[0]?.asin).toBe('B009KS9JKA')
  })

  it('searchAudibleByTitleAndAuthor still accepts a wrapped { totalResults, results } response', async () => {
    vi.resetModules()

    const wrapped = {
      totalResults: 2,
      results: [
        { asin: 'X1', title: 'One' },
        { asin: 'X2', title: 'Two' },
      ],
    }
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(wrapped), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    const response = await actual.apiService.searchAudibleByTitleAndAuthor('One', 'Anyone')

    expect(response.totalResults).toBe(2)
    expect(response.results).toHaveLength(2)
    expect(response.results[1]?.asin).toBe('X2')
  })

  it('searchAudibleByTitleAndAuthor returns an empty envelope when the server responds with null', async () => {
    vi.resetModules()

    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response('null', {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    const response = await actual.apiService.searchAudibleByTitleAndAuthor('Whatever', 'Nobody')

    expect(response.totalResults).toBe(0)
    expect(response.results).toEqual([])
  })
})
