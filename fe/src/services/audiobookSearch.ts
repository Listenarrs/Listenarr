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
// Add New's search, its rules shared with every screen that searches for a book (Add New, Repair,
// Find Match): what a query is (an ASIN, an ISBN, a title, or TITLE:/AUTHOR:/ASIN:/SERIES:/ISBN:
// tokens), where each kind is searched, in which region and language, and how many results.
import type { SearchResult } from '@/types'
import { apiService } from '@/services/api'
import { getPreferredSearchLanguageFilter } from '@/utils/languageMapping'
import { buildSeriesFields, type SeriesEntry } from '@/utils/seriesUtils'

export type AudibleMetadataRecord = Record<string, unknown> & {
  guid?: string
  id?: string
  asin?: string
  title?: string
  productTitle?: string
  size?: string | number
  image?: string
  imageUrl?: string
  coverImage?: string
  publisher?: string
  publisherName?: string
  releaseDate?: string
  publishedDate?: string
  runtimeMinutes?: number
  runtimeLengthMin?: number
  runtime?: number
  lengthMinutes?: number
  language?: string
  Language?: string
  subtitle?: string
  Subtitle?: string
  description?: string
  Description?: string
  narrators?: SearchResult['narrators']
  Narrators?: SearchResult['narrators']
  // As the metadata endpoint gives them: Audible's series (name, ASIN, position) and genres
  series?: SeriesEntry[]
  genres?: ({ name?: string } | string)[]
  publishDate?: string
  authors?: SearchResult['authors']
  Authors?: SearchResult['authors']
}

export type AudibleMetadataResponse = {
  metadata?: AudibleMetadataRecord
  source?: string
} & AudibleMetadataRecord

export type SearchType = 'asin' | 'title' | 'isbn'

// An ASIN: B and 9 letters or digits, or a 10-digit ISBN-10 (ending with a digit or X)
export const ASIN_PATTERN = /^(B[0-9A-Z]{9}|\d{9}(?:X|\d))$/i
const ISBN13_PATTERN = /^\d{13}$/
// Results asked for a title search
const TITLE_SEARCH_LIMIT = 50

export interface AudiobookSearchOptions {
  // The Audible region searched
  region: string
  // The preferred search language (e.g. 'english', or 'all' for no filter; left out, it falls back
  // to the default language, not to no filter)
  language?: string
  signal?: AbortSignal
  // How many candidates the server pools (Start Matching): asked instead of a number of results
  cap?: number
  // How many results are asked, 50 unless said otherwise
  limit?: number
}

// What a query is searched as: explicit prefixes first, then an ISBN-13, then an ASIN or ISBN-10,
// else a title
export function detectSearchType(query: string): SearchType {
  const normalizedQuery = query.trim().toUpperCase()
  if (normalizedQuery.startsWith('ASIN:')) return 'asin'
  if (normalizedQuery.startsWith('ISBN:')) return 'isbn'
  if (normalizedQuery.startsWith('AUTHOR:') || normalizedQuery.startsWith('TITLE:')) return 'title'
  if (ISBN13_PATTERN.test(normalizedQuery)) return 'isbn'
  // A strict pattern, so short title-like strings aren't taken for ASINs
  if (ASIN_PATTERN.test(normalizedQuery)) return 'asin'
  return 'title'
}

// The ASIN of an ASIN query (bare or ASIN:), uppercased; null when it isn't a valid one
export function queryAsin(query: string): string | null {
  const asinCandidate = query
    .trim()
    .replace(/^ASIN:/i, '')
    .trim()
    .toUpperCase()
  return ASIN_PATTERN.test(asinCandidate) ? asinCandidate : null
}

export interface SearchFields {
  title?: string
  author?: string
  isbn?: string
  asin?: string
  series?: string
}

// The search fields of a title query: TITLE:, AUTHOR:, ISBN:, ASIN: and SERIES: tokens (values can
// hold several words), or the whole query as the title when it has none
export function parseSearchTokens(query: string): SearchFields {
  const valuesByToken: Record<string, string> = {}
  const tokenPattern = /(TITLE:|AUTHOR:|ISBN:|ASIN:|SERIES:)/gi
  const tokenMatches: Array<{ token: string; index: number; length: number }> = []
  let patternMatch: RegExpExecArray | null
  while ((patternMatch = tokenPattern.exec(query)) !== null) {
    if (patternMatch[1])
      tokenMatches.push({
        token: patternMatch[1].toLowerCase().replace(':', ''),
        index: patternMatch.index ?? 0,
        length: patternMatch[1].length,
      })
  }
  tokenMatches.forEach((tokenMatch, matchIndex) => {
    const valueEnd = tokenMatches[matchIndex + 1]?.index ?? query.length
    const tokenValue = query.substring(tokenMatch.index + tokenMatch.length, valueEnd).trim()
    if (tokenValue) valuesByToken[tokenMatch.token] = tokenValue
  })
  return Object.keys(valuesByToken).length ? valuesByToken : { title: query }
}

// Audible's metadata for an ASIN as a search result
export function metadataToSearchResult(
  metadataResponse: AudibleMetadataResponse,
  asin: string,
): SearchResult {
  const metadataRecord =
    metadataResponse?.metadata && typeof metadataResponse.metadata === 'object'
      ? metadataResponse.metadata
      : (metadataResponse as AudibleMetadataRecord)
  const { series, seriesNumber, seriesAsin } = buildSeriesFields(metadataRecord.series)
  return {
    id: String(metadataRecord.guid ?? metadataRecord.id ?? metadataRecord.asin ?? asin),
    asin,
    title: String(metadataRecord.title ?? metadataRecord.productTitle ?? ''),
    artist: '',
    album: '',
    category: '',
    source: String(metadataResponse?.source ?? 'audible'),
    sourceLink: '',
    publishedDate: String(metadataRecord.releaseDate ?? metadataRecord.publishedDate ?? ''),
    format: '',
    size: Number(metadataRecord.size ?? 0),
    magnetLink: '',
    torrentUrl: '',
    nzbUrl: '',
    downloadType: '',
    imageUrl:
      typeof metadataRecord.image === 'string'
        ? metadataRecord.image
        : typeof metadataRecord.imageUrl === 'string'
          ? metadataRecord.imageUrl
          : typeof metadataRecord.coverImage === 'string'
            ? metadataRecord.coverImage
            : undefined,
    metadataSource: String(metadataResponse?.source ?? 'audible'),
    language:
      typeof metadataRecord.language === 'string'
        ? metadataRecord.language
        : typeof metadataRecord.Language === 'string'
          ? metadataRecord.Language
          : undefined,
    subtitle:
      typeof metadataRecord.subtitle === 'string'
        ? metadataRecord.subtitle
        : typeof metadataRecord.Subtitle === 'string'
          ? metadataRecord.Subtitle
          : undefined,
    description:
      typeof metadataRecord.description === 'string'
        ? metadataRecord.description
        : typeof metadataRecord.Description === 'string'
          ? metadataRecord.Description
          : undefined,
    publisher:
      typeof metadataRecord.publisher === 'string'
        ? metadataRecord.publisher
        : typeof metadataRecord.publisherName === 'string'
          ? metadataRecord.publisherName
          : undefined,
    runtime:
      typeof metadataRecord.runtimeMinutes === 'number'
        ? metadataRecord.runtimeMinutes
        : typeof metadataRecord.runtimeLengthMin === 'number'
          ? metadataRecord.runtimeLengthMin
          : typeof metadataRecord.lengthMinutes === 'number'
            ? metadataRecord.lengthMinutes
            : typeof metadataRecord.runtime === 'number'
              ? metadataRecord.runtime
              : undefined,
    narrators: Array.isArray(metadataRecord.narrators)
      ? (metadataRecord.narrators as SearchResult['narrators'])
      : Array.isArray(metadataRecord.Narrators)
        ? (metadataRecord.Narrators as SearchResult['narrators'])
        : undefined,
    authors: Array.isArray(metadataRecord.authors)
      ? (metadataRecord.authors as SearchResult['authors'])
      : Array.isArray(metadataRecord.Authors)
        ? (metadataRecord.Authors as SearchResult['authors'])
        : undefined,
    // The record's series, genres and dates, as a title search's results carry them: a book added
    // from this result (Library Import's Find Match) keeps them
    series,
    seriesNumber,
    seriesAsin,
    genres: Array.isArray(metadataRecord.genres)
      ? metadataRecord.genres
          .map((genre) => (typeof genre === 'string' ? genre : (genre?.name ?? '')))
          .filter(Boolean)
      : undefined,
    releaseDate:
      typeof metadataRecord.releaseDate === 'string' ? metadataRecord.releaseDate : undefined,
    publishDate:
      typeof metadataRecord.publishDate === 'string' ? metadataRecord.publishDate : undefined,
  }
}

// An ASIN goes to the metadata endpoint, which tries the enabled metadata sources in their
// priority order, in the region
export async function searchAsin(asin: string, region: string): Promise<SearchResult[]> {
  const unprefixedAsin = asin.replace(/^ASIN:/i, '').trim()
  const metadataResponse = await apiService.getAudibleMetadata<AudibleMetadataResponse>(
    unprefixedAsin,
    region,
  )
  return [metadataToSearchResult(metadataResponse, unprefixedAsin)]
}

// An ISBN: ISBN:-prefixed through the advanced search, a bare one through the simple search
export async function searchIsbn(
  query: string,
  { region, language, signal }: AudiobookSearchOptions,
): Promise<SearchResult[]> {
  const languageFilter = getPreferredSearchLanguageFilter(language)
  const prefixedIsbnMatch = query.match(/^isbn:(.+)/i)
  if (prefixedIsbnMatch?.[1]) {
    return (
      (await apiService.advancedSearch({
        isbn: prefixedIsbnMatch[1].trim(),
        region,
        ...(languageFilter ? { language: languageFilter } : {}),
        pagination: { page: 1, limit: TITLE_SEARCH_LIMIT },
      })) || []
    )
  }
  return apiService.searchByTitle(`ISBN:${query}`, {
    signal,
    region,
    ...(languageFilter ? { language: languageFilter } : {}),
  })
}

// Keep usable results only (Audible answers an ASIN it doesn't sell in that market with an empty
// stub: an ASIN but no title) and tag each one with the region it came from
export function usableInRegion(searchResults: SearchResult[], region: string): SearchResult[] {
  return searchResults
    .filter((searchResult) => !(searchResult.asin && !searchResult.title))
    .map((searchResult) => ({ ...searchResult, region: searchResult.region || region }))
}

// The search fields through the advanced search, filtered on the language
export async function searchFields(
  fields: SearchFields,
  { region, language, cap, limit = TITLE_SEARCH_LIMIT }: AudiobookSearchOptions,
): Promise<SearchResult[]> {
  const languageFilter = getPreferredSearchLanguageFilter(language)
  return (
    (await apiService.advancedSearch({
      ...fields,
      region,
      ...(languageFilter ? { language: languageFilter } : {}),
      ...(cap ? { cap } : { pagination: { page: 1, limit } }),
    })) || []
  )
}

// A series alone: the server looks the series up on Audible
export function isSeriesOnlySearch({ title, author, isbn, asin, series }: SearchFields): boolean {
  return !!series && !title && !author && !isbn && !asin
}

// Whether a result belongs to a series, by the series' ASIN or name. A result can hold its series
// as a list or as text, and its own record under searchResult
function isInSeries(searchResult: SearchResult, seriesName: string): boolean {
  const wanted = seriesName.toLowerCase()
  const holds = (value: unknown) => !!value && String(value).toLowerCase().includes(wanted)
  const nested = (searchResult as unknown as { searchResult?: SearchResult }).searchResult
  const record = nested ?? searchResult
  const series: unknown = record.series
  if (Array.isArray(series)) {
    const isListed = series.some((item: Record<string, unknown> | string) =>
      typeof item === 'string'
        ? holds(item)
        : holds(item?.asin ?? item?.Asin ?? item?.ASIN) || holds(item?.name ?? item?.Name),
    )
    if (isListed) return true
  }
  return (
    (typeof series === 'string' && holds(series)) ||
    (typeof searchResult.series === 'string' && holds(searchResult.series))
  )
}

// Add New's advanced form. A series alone is looked up as a series. With an author, the author takes
// priority: the author's books filtered on the series, or all of them when none is in it. With
// anything else the series is left out of the search
export async function searchAdvancedFields(
  { series, ...fields }: SearchFields,
  options: AudiobookSearchOptions,
): Promise<SearchResult[]> {
  if (isSeriesOnlySearch({ ...fields, series })) return searchFields({ series }, options)
  const searchResults = await searchFields(fields, options)
  if (!fields.author || !series) return searchResults
  const inSeries = searchResults.filter((searchResult) => isInSeries(searchResult, series))
  return inSeries.length ? inSeries : searchResults
}

// A title (or TITLE:/AUTHOR:/… tokens)
export function searchTitle(
  query: string,
  options: AudiobookSearchOptions,
): Promise<SearchResult[]> {
  return searchFields(parseSearchTokens(query), options)
}

// A title and an author held apart (Repair and Find Match's fields), sent as they are: no token is
// read in them, so a title holding "Series:" stays whole. An ASIN in the title is looked up, and an
// ISBN alone searched as one, as Add New does
export function searchTitleAndAuthor(
  title: string,
  author: string,
  options: AudiobookSearchOptions,
): Promise<SearchResult[]> {
  const asin = queryAsin(title)
  if (asin) return searchAsin(asin, options.region)
  if (!author && detectSearchType(title) === 'isbn') return searchIsbn(title, options)
  return searchFields({ title, ...(author ? { author } : {}) }, options)
}

// A query searched the way Add New does, whatever it holds
export async function searchAudiobooks(
  query: string,
  options: AudiobookSearchOptions,
): Promise<SearchResult[]> {
  const trimmedQuery = query.trim()
  const searchType = detectSearchType(trimmedQuery)
  if (searchType === 'asin') {
    const asin = queryAsin(trimmedQuery)
    return asin ? searchAsin(asin, options.region) : []
  }
  if (searchType === 'isbn') return searchIsbn(trimmedQuery, options)
  return searchTitle(trimmedQuery, options)
}
