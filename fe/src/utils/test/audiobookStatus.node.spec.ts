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
import type { Audiobook, QualityProfile } from '@/types'

describe('computeAudiobookStatus', () => {
  it('uses the server-provided slim status when files are not present', () => {
    const audiobook = {
      id: 1,
      title: 'Slim Book',
      status: 'quality-match',
      wanted: false,
    } as Audiobook

    expect(computeAudiobookStatus(audiobook, new Set(), [])).toBe('quality-match')
  })

  it('lets active downloads override the cached list status', () => {
    const audiobook = {
      id: 2,
      title: 'Downloading Book',
      status: 'quality-match',
      wanted: false,
    } as Audiobook

    expect(computeAudiobookStatus(audiobook, new Set([2]), [])).toBe('downloading')
  })

  it('recomputes from files when a richer audiobook payload is available', () => {
    const audiobook = {
      id: 3,
      title: 'Detailed Book',
      qualityProfileId: 10,
      files: [{ id: 100, format: 'm4b', bitrate: 320000 }],
    } as Audiobook

    const profiles: QualityProfile[] = [
      {
        id: 10,
        name: 'High Quality',
        cutoffQuality: '320kbps',
        preferredFormats: ['m4b'],
        qualities: [{ quality: '320kbps', allowed: true, priority: 0 }],
      },
    ]

    expect(computeAudiobookStatus(audiobook, new Set(), profiles)).toBe('quality-match')
  })

  it('handles bitrate values stored in bits per second', () => {
    const audiobook = {
      id: 4,
      title: 'Bitrate Book',
      qualityProfileId: 10,
      files: [{ id: 101, format: 'm4b', bitrate: 256000 }],
    } as Audiobook

    const profiles: QualityProfile[] = [
      {
        id: 10,
        name: 'High Quality',
        cutoffQuality: '256kbps',
        preferredFormats: ['m4b'],
        qualities: [{ quality: '256kbps', allowed: true, priority: 0 }],
      },
    ]

    expect(computeAudiobookStatus(audiobook, new Set(), profiles)).toBe('quality-match')
  })

  it('treats WavPack files as lossless', () => {
    const audiobook = {
      id: 5,
      title: 'Lossless Book',
      qualityProfileId: 10,
      files: [{ id: 102, format: 'wv', container: 'wv' }],
    } as Audiobook

    const profiles: QualityProfile[] = [
      {
        id: 10,
        name: 'Lossless',
        cutoffQuality: 'lossless',
        preferredFormats: ['wv'],
        qualities: [{ quality: 'lossless', allowed: true, priority: 0 }],
      },
    ]

    expect(computeAudiobookStatus(audiobook, new Set(), profiles)).toBe('quality-match')
  })
})
