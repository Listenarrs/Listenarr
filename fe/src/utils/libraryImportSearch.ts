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
import type { RecordQuery } from '@/utils/audibleRecordSearch'
import type { SearchResult } from '@/types'

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

const sameWords = (a: string[], b: Set<string>) => a.length > 0 && a.every((w) => b.has(w))

// The book name inside a folder or file name such as
// "04 Primal Hunter - Primal Hunter - Primal Hunter, Tome 4 - Zogarth":
// - "[...]" and "{...}" tags are dropped, dots and underscores become spaces in a name without spaces;
// - the name is split on " - "; a part that is the author is dropped;
// - a leading number ("04 Primal Hunter", or a part "04" on its own) is the volume: it is taken off;
// - a part whose words are all in another part is dropped (repeated series name);
// - the volume is added back when no number is left ("Primal Hunter, Tome 4" already has it).
export function cleanBookName(name: string, author?: string): string {
  let text = name.replace(/\[[^\]]*\]|\{[^}]*\}/g, ' ')
  if (!/\s/.test(text.trim())) text = text.replace(/[._]+/g, ' ')
  text = withVolumeNumber(text.replace(/\s+/g, ' ').trim())

  const authorWords = new Set(titleWords(author))
  let volume = ''
  const parts: string[] = []
  for (const raw of text.split(/\s+[-–—]\s+/)) {
    let part = raw.trim()
    const leading = /^(\d{1,3})(?:\s*[-.)]\s*|\s+)(.*)$/.exec(part)
    if (/^\d{1,3}$/.test(part)) {
      volume ||= String(Number(part))
      continue
    }
    if (leading && leading[2]) {
      volume ||= String(Number(leading[1]))
      part = leading[2].trim()
    }
    const words = titleWords(part)
    if (!words.length) continue
    if (authorWords.size && sameWords(words, authorWords)) continue
    parts.push(part)
  }

  const kept = parts.filter((part, i) => {
    const words = titleWords(part)
    return !parts.some((other, j) => {
      if (j === i) return false
      const otherWords = new Set(titleWords(other))
      // drop a part contained in a longer one; of two identical parts, keep the first
      return sameWords(words, otherWords) && (otherWords.size > words.length || j < i)
    })
  })

  const result = (kept.length ? kept : parts).join(' ').trim()
  if (!result) return text
  return volume && !/\d/.test(result) ? `${result} ${volume}` : result
}

// The book title to search: the folder name when the book has several audio files (their names are
// chapters or parts), the file name when it has only one, cleaned up (see cleanBookName). The title
// tag is not used: it is often the series name, or not read yet when matching starts.
export function buildLibraryImportTitle(
  item: Pick<
    LibraryImportSearchCandidate,
    'fullPath' | 'folderName' | 'sourceFiles' | 'fileCount' | 'detectedAuthor'
  >,
): string {
  const audioFiles = item.sourceFiles?.length || item.fileCount || 1
  const name = audioFiles > 1 ? item.folderName : fileStem(item.fullPath) || item.folderName
  return cleanBookName(name.trim(), item.detectedAuthor)
}

// What Start Matching searches for a row, and what Find Match opens with: the ASIN read from the
// files, otherwise the cleaned folder or file name with the author read from the tags
export function buildLibraryImportQuery(item: LibraryImportSearchCandidate): RecordQuery {
  const asin = item.detectedAsin?.trim()
  if (asin) return { asin }
  const author = item.detectedAuthor?.trim()
  return { title: buildLibraryImportTitle(item), ...(author ? { author } : {}) }
}

// The result Start Matching takes, or none. A result found by ASIN is the book. A result found by
// name must fit the name (Audible's keyword search also returns unrelated books); among those, the
// first one whose author fits the author read from the files, else the first one.
export function pickAutoMatch(
  results: SearchResult[],
  query: RecordQuery,
  detectedAuthor?: string,
): SearchResult | null {
  if (query.asin) return results[0] ?? null
  const fitting = results.filter((r) => titleFitsSearch(query.title ?? '', r))
  const needle = detectedAuthor?.trim().toLowerCase()
  if (needle) {
    const byAuthor = fitting.find((r) => {
      const name = (r.authors?.[0]?.name ?? '').toLowerCase()
      return name && (name.includes(needle) || needle.includes(name))
    })
    if (byAuthor) return byAuthor
  }
  return fitting[0] ?? null
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
