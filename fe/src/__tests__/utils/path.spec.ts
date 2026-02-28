import { describe, it, expect } from 'vitest'
import { toForward, trimTrailingSlash, normalizeForCompare, isAbsolutePath, stripRootPrefix } from '@/utils/path'

describe('path utils', () => {
  it('toForward converts backslashes to forward', () => {
    expect(toForward('C:\\temp\\dir')).toBe('C:/temp/dir')
    expect(toForward(null)).toBe('')
  })

  it('trimTrailingSlash removes trailing slashes', () => {
    expect(trimTrailingSlash('C:/path/')).toBe('C:/path')
    expect(trimTrailingSlash('C:\\path\\')).toBe('C:\\path')
    expect(trimTrailingSlash('no-slash')).toBe('no-slash')
  })

  it('normalizeForCompare lowercases and trims', () => {
    expect(normalizeForCompare('C:\\Temp\\Dir\\')).toBe('c:/temp/dir')
  })

  it('isAbsolutePath detects absolute paths', () => {
    expect(isAbsolutePath('C:\\some\\path')).toBe(true)
    expect(isAbsolutePath('/unix/path')).toBe(true)
    expect(isAbsolutePath('relative/path')).toBe(false)
  })

  it('stripRootPrefix removes root prefix when present', () => {
    const root = 'C:\\temp\\Isaac Asimov\\Foundation'
    const full = 'C:\\temp\\Isaac Asimov\\Foundation\\Prelude to Foundation'
    const rel = stripRootPrefix(root, full)
    expect(rel).toBe('Prelude to Foundation')

    // preserves backslash style when root uses backslashes
    const root2 = 'C:/temp/Isaac Asimov/Foundation'
    const full2 = 'C:/temp/Isaac Asimov/Foundation/Prelude to Foundation'
    const rel2 = stripRootPrefix(root2, full2)
    expect(rel2).toBe('Prelude to Foundation')

    // returns null when no match
    expect(stripRootPrefix('C:/root/other', full)).toBe(null)

    // matches using last segments
    const root3 = 'C:/temp/Isaac Asimov/Foundation/Extra'
    const full3 = 'C:/some/prefix/isaac asimov/foundation/Prelude'
    const rel3 = stripRootPrefix(root3, full3)
    expect(rel3).toBe('Prelude')
  })
})