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

// How each reason the server gives for a broken book is shown (the server decides which apply).
// The server has the same dictionary of codes (BrokenBookReasons in BrokenBookRules.cs): a backend
// test fails when the two differ, so add or remove a code on both sides.
export const BROKEN_REASON_LABELS = {
  missing_title: 'No title',
  missing_author: 'No author',
  missing_asin: 'No ASIN',
  invalid_asin: 'Not an ASIN',
  missing_region: 'No region',
} as const satisfies Record<string, string>

export type BrokenReason = keyof typeof BROKEN_REASON_LABELS

export function brokenReasonLabel(reason: string): string {
  return BROKEN_REASON_LABELS[reason as BrokenReason] ?? reason
}
