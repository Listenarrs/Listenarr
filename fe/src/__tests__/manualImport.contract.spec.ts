import { describe, expect, it } from 'vitest'
import type { Audiobook, ManualImportResult } from '@/types'

describe('manual import result contract', () => {
  it('matches the backend result shape', () => {
    const audiobook = { id: 42, title: 'Recovered Book' } as Audiobook
    const result = {
      success: true,
      sourcePath: '/incoming/book.m4b',
      destinationPath: '/library/book/book.m4b',
      audiobook,
      skipped: false,
    } satisfies ManualImportResult

    expect(result.sourcePath).toBe('/incoming/book.m4b')
    expect(result.destinationPath).toBe('/library/book/book.m4b')
    expect(result.audiobook.id).toBe(42)
  })
})
