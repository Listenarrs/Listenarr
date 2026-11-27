import { describe, it, expect } from 'vitest'
import { tokenizeTitle, transferMatchScore } from '@/utils/transferSearch'

describe('transferSearch', () => {
  it('splits Latin titles into words and drops single characters', () => {
    expect(tokenizeTitle('Warbreaker 2 of 3 (Dramatized)')).toEqual([
      'warbreaker',
      'of',
      'dramatized',
    ])
  })

  it.each([
    ['三体', ['三体']],
    ['Преступление и наказание', ['преступление', 'и', 'наказание']],
    ['الجريمة والعقاب', ['الجريمة', 'والعقاب']],
    ['Les Misérables', ['les', 'misérables']],
  ])('keeps the words of non-ASCII title %s', (title, tokens) => {
    expect(tokenizeTitle(title)).toEqual(tokens)
  })

  it('matches Cyrillic titles by shared words', () => {
    expect(
      transferMatchScore('преступление наказание 01', 'Преступление и наказание'),
    ).toBeGreaterThan(0)
    expect(transferMatchScore('Война мир', 'Преступление и наказание')).toBe(0)
  })

  it('matches a title written without spaces when the query is part of it', () => {
    expect(transferMatchScore('三体', '三体')).toBeGreaterThan(0)
    expect(transferMatchScore('黑暗森林', '三体II：黑暗森林')).toBeGreaterThan(0)
    expect(transferMatchScore('黑暗', '三体II：黑暗森林')).toBeGreaterThan(0)
    expect(transferMatchScore('流浪地球', '三体II：黑暗森林')).toBe(0)
  })

  it('ranks a contained query above a partial word overlap', () => {
    const contained = transferMatchScore('way of kings', 'The Way of Kings')
    const partial = transferMatchScore('way of kings', 'The Way Home')
    expect(contained).toBeGreaterThan(partial)
    expect(partial).toBeGreaterThan(0)
  })

  it('returns no match for an empty or symbol-only query', () => {
    expect(transferMatchScore('', 'Warbreaker')).toBe(0)
    expect(transferMatchScore('--- ::', 'Warbreaker')).toBe(0)
    expect(transferMatchScore('zzz', 'Warbreaker')).toBe(0)
  })
})
