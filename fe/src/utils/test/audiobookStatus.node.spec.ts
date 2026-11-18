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
import { describe, expect, it } from 'vitest'
import { computeAudiobookStatus } from '@/utils/audiobookStatus'
import type { Audiobook } from '@/types'

describe('computeAudiobookStatus', () => {
  it('trusts the server-computed quality-match status', () => {
    const audiobook = { id: 1, title: 'Matched', status: 'quality-match' } as Audiobook
    expect(computeAudiobookStatus(audiobook, new Set())).toBe('quality-match')
  })

  it('trusts the server-computed quality-mismatch status', () => {
    const audiobook = { id: 2, title: 'Below Cutoff', status: 'quality-mismatch' } as Audiobook
    expect(computeAudiobookStatus(audiobook, new Set())).toBe('quality-mismatch')
  })

  it('trusts the server-computed no-file status', () => {
    const audiobook = { id: 3, title: 'Missing', status: 'no-file' } as Audiobook
    expect(computeAudiobookStatus(audiobook, new Set())).toBe('no-file')
  })

  it('lets an active download override the cached server status', () => {
    const audiobook = { id: 4, title: 'Downloading', status: 'quality-match' } as Audiobook
    expect(computeAudiobookStatus(audiobook, new Set([4]))).toBe('downloading')
  })

  it('does not recompute from files/profiles — the server is the source of truth', () => {
    // Files look like a healthy 320kbps M4B, but the server says it is below cutoff
    // (e.g. its profile cutoff is higher). The frontend must defer to the server.
    const audiobook = {
      id: 5,
      title: 'Defer To Server',
      status: 'quality-mismatch',
      qualityProfileId: 10,
      files: [{ id: 100, format: 'm4b', codec: 'aac', bitrate: 320000 }],
    } as Audiobook

    expect(computeAudiobookStatus(audiobook, new Set())).toBe('quality-mismatch')
  })

  it('falls back to no-file when the server sent no recognizable status', () => {
    const audiobook = { id: 6, title: 'No Status' } as Audiobook
    expect(computeAudiobookStatus(audiobook, new Set())).toBe('no-file')
  })

  it('falls back to no-file when the status is an unrelated download-state string', () => {
    const audiobook = { id: 7, title: 'Stray Status', status: 'completed' } as unknown as Audiobook
    expect(computeAudiobookStatus(audiobook, new Set())).toBe('no-file')
  })
})
