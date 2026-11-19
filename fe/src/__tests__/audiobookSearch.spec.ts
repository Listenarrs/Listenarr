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
import type { SearchResult } from '@/types'
import { looksLikeAsin } from '@/utils/seriesUtils'

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
  isEmptyRecord,
  isSeriesOnlySearch,
  recordOfResponse,
  searchAdvancedFields,
  searchFields,
  searchTitle,
  searchTitleAndAuthor,
  usableInRegion,
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

  it('takes a ten-character, non-B0 value for an ASIN, unlike a series identifier', async () => {
    // Real Audible product ASINs are not all B0-prefixed: ISBN-shaped ones (ten digits, or nine and
    // a trailing X) are valid ten-character ASINs on the catalogue, even though a series
    // membership's seriesAsin is deliberately restricted to the B0 shape (seriesUtils). Regression
    // guard for the two detectors sharing one predicate.
    for (const asin of ['1000000001', '100000000X']) {
      expect(detectSearchType(asin)).toBe('asin')
      expect(queryAsin(asin)).toBe(asin)
      expect(looksLikeAsin(asin)).toBe(false)
    }
    apiMocks.getAudibleMetadata.mockResolvedValue({
      metadata: { asin: '1000000001', title: 'A Book' },
    })

    await searchAudiobooks('1000000001', { region: 'fr' })

    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('1000000001', 'fr')
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
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

  it("keeps an ASIN record's series, genres and dates, as a title search's results carry them", async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue({
      metadata: {
        asin: 'B0BK9W82PQ',
        title: 'Primal Hunter 4',
        series: [{ name: 'The Primal Hunter', position: '4', asin: 'B0SERIES00' }],
        genres: [{ name: 'Fantasy' }, 'LitRPG', { name: '' }],
        releaseDate: '2022-11-01',
        publishDate: '2022-10-31',
      },
    })

    const [searchResult] = await searchAudiobooks('B0BK9W82PQ', { region: 'us' })

    expect(searchResult).toEqual(
      expect.objectContaining({
        series: 'The Primal Hunter',
        seriesNumber: '4',
        seriesAsin: 'B0SERIES00',
        genres: ['Fantasy', 'LitRPG'],
        releaseDate: '2022-11-01',
        publishDate: '2022-10-31',
      }),
    )
  })

  it("reads an ASIN record's series by the shared rule: a name is no series ASIN", async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue({
      metadata: {
        asin: 'B0BK9W82PQ',
        title: 'Primal Hunter 4',
        series: [
          { name: '', asin: 'B0IGNORED0' },
          { name: 'The Primal Hunter', position: ' 4 ', asin: 'The Primal Hunter' },
        ],
      },
    })

    const [searchResult] = await searchAudiobooks('B0BK9W82PQ', { region: 'us' })

    expect(searchResult?.series).toBe('The Primal Hunter')
    expect(searchResult?.seriesNumber).toBe('4')
    expect(searchResult?.seriesAsin).toBeUndefined()
  })

  it('sends a title and an author as they are: nothing in them is read as a token', async () => {
    await searchTitleAndAuthor('The Wheel of Time Series: Book 1', 'Robert Jordan', {
      region: 'us',
    })

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'The Wheel of Time Series: Book 1',
        author: 'Robert Jordan',
      }),
    )
    expect(apiMocks.advancedSearch.mock.calls[0]?.[0]).not.toHaveProperty('series')
  })

  it('asks a pool of candidates instead of a number of results when given a cap', async () => {
    await searchFields({ title: 'Primal Hunter 4' }, { region: 'us', language: 'all', cap: 5 })

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith({
      title: 'Primal Hunter 4',
      region: 'us',
      cap: 5,
    })
  })

  it('keeps usable results only, each tagged with its region', () => {
    const results = usableInRegion(
      [
        { title: 'Primal Hunter 4', asin: 'B0BK9W82PQ' },
        { asin: 'B0ZZZZZZZZ' },
        { title: 'Found in Germany', asin: 'B0AAAAAAAA', region: 'de' },
        { title: 'No ASIN' },
      ] as SearchResult[],
      'us',
    )

    expect(results.map(({ title, region }) => [title, region])).toEqual([
      ['Primal Hunter 4', 'us'],
      ['Found in Germany', 'de'],
      ['No ASIN', 'us'],
    ])
  })

  describe("Add New's advanced form", () => {
    const options = { region: 'us', language: 'all', limit: 25 }
    const inSeries = { title: 'In', series: 'The Primal Hunter' }
    const elsewhere = { title: 'Out', series: 'Dungeon Crawler' }

    it('looks a series alone up as a series', async () => {
      await searchAdvancedFields({ series: 'Primal Hunter' }, options)

      expect(isSeriesOnlySearch({ series: 'Primal Hunter' })).toBe(true)
      expect(apiMocks.advancedSearch).toHaveBeenCalledWith({
        series: 'Primal Hunter',
        region: 'us',
        pagination: { page: 1, limit: 25 },
      })
    })

    it("filters an author's books on the series, asked without it", async () => {
      apiMocks.advancedSearch.mockResolvedValue([inSeries, elsewhere])

      const searchResults = await searchAdvancedFields(
        { author: 'Zogarth', series: 'primal hunter' },
        options,
      )

      expect(apiMocks.advancedSearch.mock.calls[0]?.[0]).not.toHaveProperty('series')
      expect(searchResults).toEqual([inSeries])
    })

    it("reads a series from a list, by name or ASIN, and from a result's own record", async () => {
      const byName = { title: 'A', series: [{ name: 'The Primal Hunter' }] }
      const byAsin = { title: 'B', series: [{ Asin: 'PRIMAL HUNTER-1' }] }
      const nested = { title: 'C', searchResult: { series: 'The Primal Hunter' } }
      apiMocks.advancedSearch.mockResolvedValue([byName, byAsin, nested, elsewhere])

      const searchResults = await searchAdvancedFields(
        { author: 'Zogarth', series: 'Primal Hunter' },
        options,
      )

      expect(searchResults).toEqual([byName, byAsin, nested])
    })

    it("keeps all the author's books when none is in the series", async () => {
      apiMocks.advancedSearch.mockResolvedValue([elsewhere])

      expect(
        await searchAdvancedFields({ author: 'Zogarth', series: 'Primal Hunter' }, options),
      ).toEqual([elsewhere])
    })

    it('leaves the series out with a title and no author', async () => {
      await searchAdvancedFields({ title: 'Book', series: 'Primal Hunter' }, options)

      expect(isSeriesOnlySearch({ title: 'Book', series: 'Primal Hunter' })).toBe(false)
      expect(apiMocks.advancedSearch.mock.calls[0]?.[0]).not.toHaveProperty('series')
    })

    it('keeps the words of a token whole', async () => {
      await searchTitle('TITLE:The Primal Hunter AUTHOR:Zogarth', options)

      expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'The Primal Hunter', author: 'Zogarth' }),
      )
    })
  })

  it("reads a record from the metadata endpoint's answer, in its envelope or not", () => {
    const record = { asin: 'B0BK9W82PQ', title: 'Primal Hunter 4' }

    expect(recordOfResponse({ metadata: record, source: 'audible' })).toBe(record)
    expect(recordOfResponse(record)).toBe(record)
    expect(recordOfResponse(null)).toEqual({})
  })

  it('tells an empty record: an ASIN without a title', () => {
    expect(isEmptyRecord({ asin: 'B0BK9W82PQ' })).toBe(true)
    expect(isEmptyRecord({ asin: 'B0BK9W82PQ', title: 'Primal Hunter 4' })).toBe(false)
    expect(isEmptyRecord({ title: 'No ASIN' })).toBe(false)
  })

  it('leaves an empty author out of a title search', async () => {
    await searchTitleAndAuthor('Primal Hunter 4', '', { region: 'us' })

    expect(apiMocks.advancedSearch.mock.calls[0]?.[0]).not.toHaveProperty('author')
  })

  it('looks an ASIN in the title up on its own, even with an author', async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue({
      metadata: { asin: 'B0BK9W82PQ', title: 'Primal Hunter 4' },
    })

    await searchTitleAndAuthor('b0bk9w82pq', 'Zogarth', { region: 'us' })

    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('B0BK9W82PQ', 'us')
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
  })

  it('searches an ISBN alone in the title as an ISBN, and as a title with an author', async () => {
    await searchTitleAndAuthor('9780547928227', '', { region: 'us' })
    expect(apiMocks.searchByTitle).toHaveBeenCalledWith('ISBN:9780547928227', expect.anything())
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()

    await searchTitleAndAuthor('9780547928227', 'Tolkien', { region: 'us' })
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: '9780547928227', author: 'Tolkien' }),
    )
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
