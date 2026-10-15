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
export interface LibraryImportSearchCandidate {
  fullPath: string
  folderName: string
  detectedTitle?: string
  detectedAuthor?: string
  detectedAsin?: string
  // Audio files of the book (one row per book)
  sourceFiles?: string[]
  fileCount?: number
}

// "Name (3)" → "Name 3": a trailing number in brackets is usually the volume
function withVolumeNumber(name: string): string {
  const numericMatch = /\((\d+)\)\s*$/.exec(name)
  return numericMatch ? `${name.replace(/\s*\(\d+\)\s*$/, '').trim()} ${numericMatch[1]}` : name
}

function fileStem(fullPath: string): string {
  return (
    fullPath
      .replace(/\\/g, '/')
      .split('/')
      .pop()
      ?.replace(/\.[^.]+$/, '') ?? ''
  )
}

// The book title to search: the folder name when the book has several audio files (their names are
// chapters or parts), the file name when it has only one. The title tag is not used: it is often the
// series name, or not read yet when matching starts.
export function buildLibraryImportTitle(
  item: Pick<LibraryImportSearchCandidate, 'fullPath' | 'folderName' | 'sourceFiles' | 'fileCount'>,
): string {
  const audioFiles = item.sourceFiles?.length || item.fileCount || 1
  const name = audioFiles > 1 ? item.folderName : fileStem(item.fullPath) || item.folderName
  return withVolumeNumber(name.trim())
}

export function buildLibraryImportInitialQuery(item: LibraryImportSearchCandidate): string {
  const asin = item.detectedAsin?.trim()
  if (asin) return asin

  return buildLibraryImportTitle(item)
}

export function buildLibraryImportInitialAuthor(item: LibraryImportSearchCandidate): string {
  if (item.detectedAsin?.trim()) return ''
  return item.detectedAuthor?.trim() ?? ''
}

export function buildLibraryImportSearchParams(
  item: LibraryImportSearchCandidate,
  cap: number,
): {
  asin?: string
  title?: string
  author?: string
  cap: number
} {
  const asin = item.detectedAsin?.trim()
  if (asin) {
    return { asin, cap }
  }

  const title = buildLibraryImportInitialQuery(item)
  const author = buildLibraryImportInitialAuthor(item) || undefined

  return {
    title,
    ...(author ? { author } : {}),
    cap,
  }
}

// Words of a title, ignoring case, accents and punctuation; numbers lose their leading zeros so
// "04" and "4" are the same word
function titleWords(text: string | undefined | null): string[] {
  return (text ?? '')
    .normalize('NFD')
    .replace(/\p{M}/gu, '')
    .toLowerCase()
    .split(/[^\p{L}\p{N}]+/u)
    .filter(Boolean)
    .map((word) => (/^\d+$/.test(word) ? String(Number(word)) : word))
}

type TitledResult = {
  title?: string
  subtitle?: string
  series?: unknown
  seriesNumber?: string
}

// Whether a search result is plausibly the book searched by name. Audible's keyword search returns
// loose results ("not a match" finds "The Perfect Match"), so an automatic match needs one of:
// - every word of the result's title is in the searched name (the name may carry more: a track
//   number, the series, the author), or
// - every searched word is in the result's title, subtitle or series.
export function titleFitsSearch(searchedTitle: string, result: TitledResult): boolean {
  const searched = new Set(titleWords(searchedTitle))
  const title = titleWords(result.title)
  if (!searched.size || !title.length) return false
  if (title.every((word) => searched.has(word))) return true

  const series = Array.isArray(result.series)
    ? (result.series as Array<{ name?: string; position?: string }>).flatMap((s) => [
        s?.name,
        s?.position,
      ])
    : [result.series as string | undefined]
  const record = new Set(
    [result.title, result.subtitle, result.seriesNumber, ...series].flatMap((part) =>
      titleWords(typeof part === 'string' ? part : undefined),
    ),
  )
  return [...searched].every((word) => record.has(word))
}
