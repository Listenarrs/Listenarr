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
  authors?: SearchResult['authors']
  Authors?: SearchResult['authors']
}

export type AudibleMetadataResponse = {
  metadata?: AudibleMetadataRecord
  source?: string
} & AudibleMetadataRecord

export type SearchType = 'asin' | 'title' | 'isbn'

// An ASIN: B and 9 letters or digits, or a 10-digit ISBN-10 (ending with a digit or X)
const ASIN_OR_ISBN10_PATTERN = /^(B[0-9A-Z]{9}|\d{9}(?:X|\d))$/
const ISBN13_PATTERN = /^\d{13}$/
// Results asked for a title search
const TITLE_SEARCH_LIMIT = 50

export interface AudiobookSearchOptions {
  // The Audible region searched
  region: string
  // The preferred search language (e.g. 'english', or 'all' for no filter)
  language?: string
  signal?: AbortSignal
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
  if (ASIN_OR_ISBN10_PATTERN.test(normalizedQuery)) return 'asin'
  return 'title'
}

// The ASIN of an ASIN query (bare or ASIN:), uppercased; null when it isn't a valid one
export function queryAsin(query: string): string | null {
  const asinCandidate = query
    .trim()
    .replace(/^ASIN:/i, '')
    .trim()
    .toUpperCase()
  return ASIN_OR_ISBN10_PATTERN.test(asinCandidate) ? asinCandidate : null
}

// The search fields of a title query: TITLE:, AUTHOR:, ISBN:, ASIN: and SERIES: tokens (values can
// hold several words), or the whole query as the title when it has none
export function parseSearchTokens(query: string): {
  title?: string
  author?: string
  isbn?: string
  asin?: string
  series?: string
} {
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

// A title (or TITLE:/AUTHOR:/… tokens) through the advanced search, filtered on the language
export async function searchTitle(
  query: string,
  { region, language }: AudiobookSearchOptions,
): Promise<SearchResult[]> {
  const languageFilter = getPreferredSearchLanguageFilter(language)
  return (
    (await apiService.advancedSearch({
      ...parseSearchTokens(query),
      region,
      ...(languageFilter ? { language: languageFilter } : {}),
      pagination: { page: 1, limit: TITLE_SEARCH_LIMIT },
    })) || []
  )
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
