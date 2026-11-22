import { describe, expect, it } from 'vitest'
import { destinationsEqual } from '@/utils/path'

describe('destination path comparison', () => {
  it.each([
    ['/books/Title', '/books/Title/', true],
    ['/books/Title///', '/books/Title', true],
    ['/', '///', true],
    ['/', '', false],
    ['C:/', 'C:', false],
    ['C:/Books/Title', 'c:\\books\\Title\\', true],
    ['C:/Books/Title', 'C:/Books/Other', false],
    ['/books/Title', '/books/title', false],
    ['\\\\server\\share\\', '//SERVER/share', true],
  ])('compares %s and %s', (left, right, expected) => {
    expect(destinationsEqual(left, right)).toBe(expected)
  })
})
