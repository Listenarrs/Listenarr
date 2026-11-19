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

import type { SearchContext } from '@/stores/addNewSearch'
import { preferredSearchLanguageOptions, searchRegionOptions } from '@/utils/languageMapping'

const ADVANCED_FIELD_LABELS = {
  title: 'Title',
  author: 'Author',
  series: 'Series',
  isbn: 'ISBN',
  asin: 'ASIN',
} as const

// What a search was made with, in words: "for “Dune” · United States (US) · English", or the
// advanced form's fields ("Title: Dune, Author: Herbert · …")
export function describeSearchContext(context: SearchContext): string {
  const parts: string[] = []
  const query = context.query?.trim()
  if (query) parts.push(`for “${query}”`)
  else if (context.advanced) {
    const fields = (Object.keys(ADVANCED_FIELD_LABELS) as Array<keyof typeof ADVANCED_FIELD_LABELS>)
      .map((key) => [ADVANCED_FIELD_LABELS[key], context.advanced?.[key]?.trim()] as const)
      .filter(([, value]) => value)
      .map(([label, value]) => `${label}: ${value}`)
    if (fields.length) parts.push(`for ${fields.join(', ')}`)
  }
  if (context.region) {
    parts.push(
      searchRegionOptions.find((o) => o.value === context.region)?.label ??
        context.region.toUpperCase(),
    )
  }
  if (context.language) {
    parts.push(
      preferredSearchLanguageOptions.find((o) => o.value === context.language)?.label ??
        context.language,
    )
  }
  return parts.join(' · ')
}
