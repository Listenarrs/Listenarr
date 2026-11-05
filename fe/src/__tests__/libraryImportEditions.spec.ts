import { describe, expect, it } from 'vitest'
import type { SearchResult } from '@/types'
import {
  formatRuntime,
  mergeEditionResults,
  pickByRuntime,
  runtimeDeltaMinutes,
  runtimeMatches,
} from '@/utils/libraryImportEditions'

const result = (asin: string, lengthMinutes?: number, source = 'Audible') =>
  ({ asin, lengthMinutes, source, title: 'Guards! Guards!' }) as unknown as SearchResult

describe('library import editions', () => {
  it('compares an edition runtime with the file length', () => {
    expect(runtimeDeltaMinutes(result('A', 608), 608 * 60)).toBe(0)
    expect(runtimeDeltaMinutes(result('A', 629), 608 * 60)).toBe(21)
    expect(runtimeDeltaMinutes(result('A'), 608 * 60)).toBeUndefined()
    expect(runtimeDeltaMinutes(result('A', 608))).toBeUndefined()
  })

  it('treats runtimes within 3% of the file as the same recording', () => {
    expect(runtimeMatches(result('A', 608), 607 * 60)).toBe(true)
    expect(runtimeMatches(result('A', 629), 607 * 60)).toBe(false)
    expect(runtimeMatches(result('A', 608))).toBe(false)
  })

  it('appends editions Audible did not return, keeping the Audible entry for a shared ASIN', () => {
    const merged = mergeEditionResults(
      [result('B09M8W9JY5', 809), result('b004fu8xpa', 608)],
      [result('B004FU8XPA', 608, 'Audiosilo'), result('B004EW9L14', 629, 'Audiosilo')],
    )
    expect(merged.map((r) => [r.asin, r.source])).toEqual([
      ['B09M8W9JY5', 'Audible'],
      ['b004fu8xpa', 'Audible'],
      ['B004EW9L14', 'Audiosilo'],
    ])
  })

  it('picks the closest runtime within tolerance, or nothing when the length is unknown or no edition fits', () => {
    const results = [result('FULLCAST', 809), result('PLANER89', 629), result('PLANER07', 608)]
    expect(pickByRuntime(results, 607 * 60)?.asin).toBe('PLANER07')
    expect(pickByRuntime(results, 400 * 60)).toBeUndefined()
    expect(pickByRuntime(results, undefined)).toBeUndefined()
  })

  it('formats runtimes', () => {
    expect(formatRuntime(608)).toBe('10h 8m')
    expect(formatRuntime(45)).toBe('45m')
    expect(formatRuntime(undefined)).toBe('')
  })
})
