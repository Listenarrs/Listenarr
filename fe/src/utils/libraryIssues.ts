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
import type { LibraryIssueCode } from '@/types'

// The problems of a book's Audible identity, as GET /library/issues reports them
// (LibraryIssueService): shown on the Issues page and in Repair

export const ISSUE_LABELS: Record<LibraryIssueCode, string> = {
  missing_title: 'No title',
  missing_asin: 'No ASIN',
  invalid_asin: 'Invalid ASIN',
  missing_region: 'No region',
}

export function issueLabel(code: LibraryIssueCode): string {
  return ISSUE_LABELS[code] ?? code
}

// The same rules as the server, for a book whose ASIN and region are known
export function bookIssueCodes(book: {
  title?: string | null
  asin?: string | null
  region?: string | null
}): LibraryIssueCode[] {
  const issues: LibraryIssueCode[] = []
  if (!book.title?.trim()) issues.push('missing_title')
  const asin = book.asin?.trim() ?? ''
  if (!asin) issues.push('missing_asin')
  else if (!/^[A-Z0-9]{10}$/i.test(asin)) issues.push('invalid_asin')
  else if (!book.region?.trim()) issues.push('missing_region')
  return issues
}
