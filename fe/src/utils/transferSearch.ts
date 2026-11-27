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
/**
 * Destination ranking for "move files to another book".
 *
 * A query is usually a file name or a spoken title, so it rarely is a substring of
 * the record's title. Matching is by shared words, in any script: text is split on
 * anything that is not a letter or a number. Scripts written without spaces (Chinese,
 * Japanese, Thai) produce one long token per title, which a shorter query can never
 * equal, so a query contained in the title also counts.
 */

const SINGLE_LATIN_CHARACTER = /^[a-z0-9]$/

function normalize(text: string): string {
  return (text || '').normalize('NFKC').toLowerCase()
}

/** Words of a title or query. A lone Latin letter or digit carries no signal and is dropped. */
export function tokenizeTitle(text: string): string[] {
  return normalize(text)
    .split(/[^\p{L}\p{N}]+/u)
    .filter((token) => token.length > 0 && !SINGLE_LATIN_CHARACTER.test(token))
}

/** The text with everything but letters and numbers removed, for containment matching. */
function compact(text: string): string {
  return normalize(text).replace(/[^\p{L}\p{N}]+/gu, '')
}

/**
 * How well a title matches a query; 0 means no match. One point per query word found
 * in the title, and a query contained in the title outranks any partial word overlap.
 */
export function transferMatchScore(query: string, title: string): number {
  const queryTokens = tokenizeTitle(query)
  if (queryTokens.length === 0) return 0

  const titleTokens = new Set(tokenizeTitle(title))
  const overlap = queryTokens.reduce((score, token) => score + (titleTokens.has(token) ? 1 : 0), 0)

  const compactQuery = compact(query)
  const isContained = compactQuery.length > 1 && compact(title).includes(compactQuery)
  return isContained ? queryTokens.length + 1 : overlap
}
