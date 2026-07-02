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
import type { Audiobook, VerificationDetail, VerificationStatus } from '@/types'

// Shared presentation mapping for the audio-verification badge (ADR-0001),
// used by the Books list and the book detail page so the two never disagree.

export function verificationLabel(status: VerificationStatus | undefined): string {
  switch (status) {
    case 'agentVerified':
      return 'Audio verified'
    case 'agentFlagged':
      return 'Needs review'
    case 'manuallyVerified':
      return 'Verified (manual)'
    case 'rejected':
      return 'Rejected'
    case 'agentUnverifiable':
      return 'No spoken credits'
    default:
      return 'Unverified'
  }
}

// CSS modifier class; the views provide the matching styles.
export function verificationClass(status: VerificationStatus | undefined): string {
  switch (status) {
    case 'agentVerified':
    case 'manuallyVerified':
      return 'verification-ok'
    case 'agentFlagged':
      return 'verification-flagged'
    case 'rejected':
      return 'verification-rejected'
    case 'agentUnverifiable':
      return 'verification-neutral'
    default:
      return 'verification-none'
  }
}

// "Needs review" = anything an agent flagged (mismatch OR uncertain — the
// detail JSON distinguishes which) that a human hasn't ruled on yet.
export function needsReview(book: Pick<Audiobook, 'verificationStatus'>): boolean {
  return book.verificationStatus === 'agentFlagged'
}

export function parseVerificationDetail(
  book: Pick<Audiobook, 'verificationDetailJson'>,
): VerificationDetail | null {
  if (!book.verificationDetailJson) return null
  try {
    return JSON.parse(book.verificationDetailJson) as VerificationDetail
  } catch {
    return null
  }
}
