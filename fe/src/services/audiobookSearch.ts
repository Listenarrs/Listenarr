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
const ASIN_OR_ISBN10 = /^(B[0-9A-Z]{9}|\d{9}(?:X|\d))$/
const ISBN13 = /^\d{13}$/
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
  const trimmed = query.trim().toUpperCase()
  if (trimmed.startsWith('ASIN:')) return 'asin'
  if (trimmed.startsWith('ISBN:')) return 'isbn'
  if (trimmed.startsWith('AUTHOR:') || trimmed.startsWith('TITLE:')) return 'title'
  if (ISBN13.test(trimmed)) return 'isbn'
  // A strict pattern, so short title-like strings aren't taken for ASINs
  if (ASIN_OR_ISBN10.test(trimmed)) return 'asin'
  return 'title'
}

// The ASIN of an ASIN query (bare or ASIN:), uppercased; null when it isn't a valid one
export function queryAsin(query: string): string | null {
  const value = query
    .trim()
    .replace(/^ASIN:/i, '')
    .trim()
    .toUpperCase()
  return ASIN_OR_ISBN10.test(value) ? value : null
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
  const fields: Record<string, string> = {}
  const regex = /(TITLE:|AUTHOR:|ISBN:|ASIN:|SERIES:)/gi
  const matches: Array<{ key: string; index: number; len: number }> = []
  let match: RegExpExecArray | null
  while ((match = regex.exec(query)) !== null) {
    if (match[1])
      matches.push({
        key: match[1].toLowerCase().replace(':', ''),
        index: match.index ?? 0,
        len: match[1].length,
      })
  }
  matches.forEach((m, i) => {
    const end = matches[i + 1]?.index ?? query.length
    const value = query.substring(m.index + m.len, end).trim()
    if (value) fields[m.key] = value
  })
  return Object.keys(fields).length ? fields : { title: query }
}

// Audible's metadata for an ASIN as a search result
export function metadataToSearchResult(
  metaResp: AudibleMetadataResponse,
  cleanAsin: string,
): SearchResult {
  const meta =
    metaResp?.metadata && typeof metaResp.metadata === 'object'
      ? metaResp.metadata
      : (metaResp as AudibleMetadataRecord)
  return {
    id: String(meta.guid ?? meta.id ?? meta.asin ?? cleanAsin),
    asin: cleanAsin,
    title: String(meta.title ?? meta.productTitle ?? ''),
    artist: '',
    album: '',
    category: '',
    source: String(metaResp?.source ?? 'audible'),
    sourceLink: '',
    publishedDate: String(meta.releaseDate ?? meta.publishedDate ?? ''),
    format: '',
    size: Number(meta.size ?? 0),
    magnetLink: '',
    torrentUrl: '',
    nzbUrl: '',
    downloadType: '',
    imageUrl:
      typeof meta.image === 'string'
        ? meta.image
        : typeof meta.imageUrl === 'string'
          ? meta.imageUrl
          : typeof meta.coverImage === 'string'
            ? meta.coverImage
            : undefined,
    metadataSource: String(metaResp?.source ?? 'audible'),
    language:
      typeof meta.language === 'string'
        ? meta.language
        : typeof meta.Language === 'string'
          ? meta.Language
          : undefined,
    subtitle:
      typeof meta.subtitle === 'string'
        ? meta.subtitle
        : typeof meta.Subtitle === 'string'
          ? meta.Subtitle
          : undefined,
    description:
      typeof meta.description === 'string'
        ? meta.description
        : typeof meta.Description === 'string'
          ? meta.Description
          : undefined,
    publisher:
      typeof meta.publisher === 'string'
        ? meta.publisher
        : typeof meta.publisherName === 'string'
          ? meta.publisherName
          : undefined,
    runtime:
      typeof meta.runtimeMinutes === 'number'
        ? meta.runtimeMinutes
        : typeof meta.runtimeLengthMin === 'number'
          ? meta.runtimeLengthMin
          : typeof meta.lengthMinutes === 'number'
            ? meta.lengthMinutes
            : typeof meta.runtime === 'number'
              ? meta.runtime
              : undefined,
    narrators: Array.isArray(meta.narrators)
      ? (meta.narrators as SearchResult['narrators'])
      : Array.isArray(meta.Narrators)
        ? (meta.Narrators as SearchResult['narrators'])
        : undefined,
    authors: Array.isArray(meta.authors)
      ? (meta.authors as SearchResult['authors'])
      : Array.isArray(meta.Authors)
        ? (meta.Authors as SearchResult['authors'])
        : undefined,
  }
}

// An ASIN goes to the metadata endpoint, which tries the enabled metadata sources in their
// priority order, in the region
export async function searchAsin(asin: string, region: string): Promise<SearchResult[]> {
  const cleanAsin = asin.replace(/^ASIN:/i, '').trim()
  const metaResp = await apiService.getAudibleMetadata<AudibleMetadataResponse>(cleanAsin, region)
  return [metadataToSearchResult(metaResp, cleanAsin)]
}

// An ISBN: ISBN:-prefixed through the advanced search, a bare one through the simple search
export async function searchIsbn(
  query: string,
  { region, language, signal }: AudiobookSearchOptions,
): Promise<SearchResult[]> {
  const languageFilter = getPreferredSearchLanguageFilter(language)
  const prefixed = query.match(/^isbn:(.+)/i)
  if (prefixed?.[1]) {
    return (
      (await apiService.advancedSearch({
        isbn: prefixed[1].trim(),
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
  const trimmed = query.trim()
  const type = detectSearchType(trimmed)
  if (type === 'asin') {
    const asin = queryAsin(trimmed)
    return asin ? searchAsin(asin, options.region) : []
  }
  if (type === 'isbn') return searchIsbn(trimmed, options)
  return searchTitle(trimmed, options)
}
