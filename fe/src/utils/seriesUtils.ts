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
import type { Audiobook, AudiobookSeriesMembership } from '@/types'

type SeriesBearer = Pick<Audiobook, 'series' | 'seriesNumber' | 'seriesMemberships'>

/**
 * All series a book belongs to, formatted for display (e.g. "Publication Order #1,
 * Chronological Order #3"). Uses every series membership so a multi-series book shows all of
 * them; falls back to the legacy single series/number when no memberships are present.
 */
export function formatSeriesMemberships(book: SeriesBearer): string {
  const memberships = book.seriesMemberships
  if (memberships && memberships.length > 0) {
    const parts = memberships.map(formatMembership).filter(Boolean)
    if (parts.length > 0) return parts.join(', ')
  }

  const legacyName = (book.series || '').trim()
  if (!legacyName) return ''
  const legacyNumber = (book.seriesNumber || '').trim()
  return legacyNumber ? `${legacyName} #${legacyNumber}` : legacyName
}

function formatMembership(membership: AudiobookSeriesMembership): string {
  const name = (membership.seriesName || '').trim()
  if (!name) return ''
  const number = (membership.seriesNumber || '').trim()
  return number ? `${name} #${number}` : name
}

/** A series entry as the search endpoint and the product lookup both return it. */
export interface SeriesEntry {
  asin?: string
  name?: string
  position?: string
}

/** Series data in the shape AudibleBookMetadata expects, memberships and legacy scalars alike. */
export interface SeriesFields {
  seriesMemberships?: AudiobookSeriesMembership[]
  series?: string
  seriesNumber?: string
  seriesAsin?: string
}

// An Audible digital ASIN is the literal prefix B0 followed by eight alphanumerics. A bare
// ten-character check is not enough: single-word series names of that length, Foundation
// among them, pass it and then get stored as if they were identifiers.
const ASIN_PATTERN = /^B0[A-Z0-9]{8}$/i

export function looksLikeAsin(value: string | undefined | null): boolean {
  const trimmed = value?.trim()
  return Boolean(trimmed && ASIN_PATTERN.test(trimmed))
}

/**
 * A book can belong to more than one series, so every entry becomes a membership, ordered,
 * with the first marked primary. The legacy scalars come off that primary membership so the
 * two can never disagree.
 */
export function buildSeriesFields(entries: SeriesEntry[] | undefined | null): SeriesFields {
  const seriesMemberships: AudiobookSeriesMembership[] = (entries ?? [])
    .filter((entry) => (entry?.name ?? '').trim().length > 0)
    .map((entry, index) => {
      const asin = entry.asin?.trim()
      return {
        seriesName: (entry.name ?? '').trim(),
        seriesNumber: entry.position?.trim() || undefined,
        // The search fallback branch fills `asin` with the series *name* when the ASIN
        // re-fetch fails, so only keep a value that actually looks like an ASIN.
        seriesAsin: looksLikeAsin(asin) ? asin : undefined,
        isPrimary: index === 0,
        sortOrder: index,
      }
    })

  const primary = seriesMemberships[0]
  return primary
    ? {
        seriesMemberships,
        series: primary.seriesName,
        seriesNumber: primary.seriesNumber,
        seriesAsin: primary.seriesAsin,
      }
    : {}
}
