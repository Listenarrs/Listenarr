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
import type { Audiobook, AudiobookStatus } from '@/types'

const AUDIOBOOK_STATUSES: AudiobookStatus[] = [
  'downloading',
  'no-file',
  'quality-mismatch',
  'quality-match',
]

function isAudiobookStatus(value: unknown): value is AudiobookStatus {
  return typeof value === 'string' && AUDIOBOOK_STATUSES.includes(value as AudiobookStatus)
}

export function formatAudiobookStatus(status: AudiobookStatus): string {
  switch (status) {
    case 'downloading':
      return 'Downloading'
    case 'no-file':
      return 'Missing'
    case 'quality-mismatch':
      return 'Below Cutoff'
    case 'quality-match':
      return 'Downloaded'
    default:
      return ''
  }
}

/**
 * Resolve the display status for an audiobook.
 *
 * The backend is the single source of truth for quality matching: the `/library`
 * list endpoint runs {@link AudiobookStatusEvaluator.ComputeStatus} server-side and
 * returns the result as `audiobook.status`. The frontend trusts that value and only
 * applies a client-side override for transient state the server's snapshot can't see —
 * an in-flight download for this audiobook.
 *
 * This deliberately no longer mirrors the backend QualityMatcher client-side; that
 * duplicated business logic and could disagree with the automatic-search cutoff.
 */
export function computeAudiobookStatus(
  audiobook: Pick<Audiobook, 'id' | 'status'>,
  activeDownloadAudiobookIds: ReadonlySet<number>,
): AudiobookStatus {
  if (activeDownloadAudiobookIds.has(audiobook.id)) {
    return 'downloading'
  }

  if (isAudiobookStatus(audiobook.status)) {
    return audiobook.status
  }

  return 'no-file'
}
