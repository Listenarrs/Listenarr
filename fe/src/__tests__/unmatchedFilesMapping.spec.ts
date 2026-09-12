/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { describe, expect, it } from 'vitest'
import { mapToAudible } from '@/components/feedback/UnmatchedFilesModal.vue'
import type { UnmatchedFileItem } from '@/types'

const fallback = {
  fullPath: '/library/Book/01.m4b',
  bookFolder: '/library/Book',
  relativePath: 'Book',
  title: 'Tagged Title',
  author: 'Tagged Author',
  series: 'Tagged Series',
  seriesNumber: '9',
  fileCount: 1,
  format: 'M4B',
} as unknown as UnmatchedFileItem

describe('unmatched files Audible mapping', () => {
  it('reads the Audible series fields and keeps every membership', () => {
    const metadata = mapToAudible(
      {
        asin: 'B000000001',
        title: 'The Final Empire',
        series: [
          { asin: 'B01E633FQM', name: 'First Series', position: '0' },
          { asin: 'B01F5TL5K4', name: 'Second Series', position: '7' },
        ],
      },
      fallback,
    )

    expect(metadata.series).toBe('First Series')
    expect(metadata.seriesNumber).toBe('0')
    expect(metadata.seriesAsin).toBe('B01E633FQM')
    expect(metadata.seriesMemberships).toEqual([
      {
        seriesName: 'First Series',
        seriesNumber: '0',
        seriesAsin: 'B01E633FQM',
        isPrimary: true,
        sortOrder: 0,
      },
      {
        seriesName: 'Second Series',
        seriesNumber: '7',
        seriesAsin: 'B01F5TL5K4',
        isPrimary: false,
        sortOrder: 1,
      },
    ])
  })

  it('falls back to the file tags when the product has no series', () => {
    const metadata = mapToAudible({ asin: 'B000000001', title: 'The Final Empire' }, fallback)

    expect(metadata.series).toBe('Tagged Series')
    expect(metadata.seriesNumber).toBe('9')
    expect(metadata.seriesAsin).toBeUndefined()
    expect(metadata.seriesMemberships).toBeUndefined()
  })
})
