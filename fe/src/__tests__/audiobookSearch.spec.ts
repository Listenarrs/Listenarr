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
import { beforeEach, describe, expect, it, vi } from 'vitest'

const apiMocks = vi.hoisted(() => ({
  advancedSearch: vi.fn(),
  searchByTitle: vi.fn(),
  getAudibleMetadata: vi.fn(),
}))

vi.mock('@/services/api', () => ({ apiService: apiMocks }))

import {
  detectSearchType,
  parseSearchTokens,
  queryAsin,
  searchAudiobooks,
} from '@/services/audiobookSearch'

describe('audiobookSearch (Add New, Repair and Find Match)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    apiMocks.advancedSearch.mockResolvedValue([])
    apiMocks.searchByTitle.mockResolvedValue([])
  })

  it('tells an ASIN, an ISBN and a title apart, prefixes first', () => {
    expect(detectSearchType('B0BK9W82PQ')).toBe('asin')
    expect(detectSearchType('asin:b0bk9w82pq')).toBe('asin')
    expect(detectSearchType('1980006520')).toBe('asin')
    expect(detectSearchType('9780547928227')).toBe('isbn')
    expect(detectSearchType('ISBN:0547928220')).toBe('isbn')
    expect(detectSearchType('TITLE:B0BK9W82PQ')).toBe('title')
    expect(detectSearchType('The Hobbit')).toBe('title')
  })

  it('reads the ASIN of an ASIN query, bare or prefixed, and nothing else', () => {
    expect(queryAsin(' asin: b0bk9w82pq ')).toBe('B0BK9W82PQ')
    expect(queryAsin('B0BK9W82PQ')).toBe('B0BK9W82PQ')
    expect(queryAsin('B0BK9')).toBeNull()
    expect(queryAsin('Primal Hunter')).toBeNull()
  })

  it('reads TITLE:, AUTHOR:, SERIES:, ISBN: and ASIN: tokens, or takes the query as a title', () => {
    expect(
      parseSearchTokens('TITLE:Primal Hunter, Vol. 7 AUTHOR:Zogarth SERIES:The Primal Hunter'),
    ).toEqual({ title: 'Primal Hunter, Vol. 7', author: 'Zogarth', series: 'The Primal Hunter' })
    expect(parseSearchTokens('The Hobbit')).toEqual({ title: 'The Hobbit' })
  })

  it('searches a title in the region and its language, up to 50 results', async () => {
    await searchAudiobooks('TITLE:Primal Hunter AUTHOR:Zogarth', {
      region: 'us',
      language: 'english',
    })

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith({
      title: 'Primal Hunter',
      author: 'Zogarth',
      region: 'us',
      language: 'english',
      pagination: { page: 1, limit: 50 },
    })
  })

  it('leaves the language out when every language is wanted', async () => {
    await searchAudiobooks('The Hobbit', { region: 'us', language: 'all' })

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.not.objectContaining({ language: expect.anything() }),
    )
  })

  it('looks an ASIN up through the metadata sources, in the region', async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue({
      metadata: { asin: 'B0BK9W82PQ', title: 'Primal Hunter 4', subtitle: 'A LitRPG Adventure' },
      source: 'audible',
    })

    const searchResults = await searchAudiobooks('ASIN:b0bk9w82pq', { region: 'us' })

    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('B0BK9W82PQ', 'us')
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(searchResults).toEqual([
      expect.objectContaining({
        asin: 'B0BK9W82PQ',
        title: 'Primal Hunter 4',
        subtitle: 'A LitRPG Adventure',
      }),
    ])
  })

  it('searches an ISBN: prefixed through the advanced search, bare through the simple one', async () => {
    await searchAudiobooks('ISBN:9780547928227', { region: 'us', language: 'english' })
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ isbn: '9780547928227', region: 'us', language: 'english' }),
    )

    await searchAudiobooks('9780547928227', { region: 'fr', language: 'french' })
    expect(apiMocks.searchByTitle).toHaveBeenCalledWith(
      'ISBN:9780547928227',
      expect.objectContaining({ region: 'fr', language: 'french' }),
    )
  })
})
