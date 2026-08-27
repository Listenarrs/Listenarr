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

describe('ApiService searchAudibleByTitleAndAuthor', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('unwraps a bare JSON array response (the shape /search Advanced mode actually returns)', async () => {
    // Live false-flag: the "find correct match" modal reported "No matches in
    // any region" for a book Audible genuinely had, in every region, every
    // time — because this function only read response.results, and the
    // backend's Advanced-search branch returns a bare array, not
    // {results, totalResults}. Every other caller of this same endpoint
    // (getAuthorLookup, advancedSearch) already handled both shapes.
    vi.resetModules()

    const candidate = { asin: 'B002VA9GXE', title: 'Dark Lover', region: 'us' }
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify([candidate]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    const response = await actual.apiService.searchAudibleByTitleAndAuthor(
      'Dark Lover',
      'J.R. Ward',
      1,
      25,
      'us',
    )

    expect(response.totalResults).toBe(1)
    expect(response.results).toEqual([candidate])
  })

  it('still works if the backend wraps results in an object', async () => {
    vi.resetModules()

    const candidate = { asin: 'B002VA9GXE', title: 'Dark Lover', region: 'us' }
    const fetchMock = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify({ totalResults: 1, results: [candidate] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    const response = await actual.apiService.searchAudibleByTitleAndAuthor(
      'Dark Lover',
      'J.R. Ward',
      1,
      25,
      'us',
    )

    expect(response.totalResults).toBe(1)
    expect(response.results).toEqual([candidate])
  })
})
