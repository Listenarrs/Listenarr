import { describe, it, expect } from 'vitest'
import {
  extractAuthors,
  extractPublishedDate,
  extractNarrators,
  normalizeRuntime,
  processSeries,
  normalizeSource,
  isAudimetaSource,
  getPrimaryId,
  extractSubtitle,
  extractDescription,
  extractPublishers,
  extractLanguage,
  normalizeResultMetadata,
  type NormalizedResult,
} from '@/utils/searchResultHelpers'

describe('searchResultHelpers', () => {
  describe('extractAuthors', () => {
    it('extracts single author from string', () => {
      expect(extractAuthors({ author: 'Frank Herbert' })).toEqual(['Frank Herbert'])
    })

    it('handles multiple author formats', () => {
      expect(extractAuthors({ Artist: 'Stephen King' })).toEqual(['Stephen King'])
      expect(extractAuthors({ artist: 'J.R.R. Tolkien' })).toEqual(['J.R.R. Tolkien'])
      expect(extractAuthors({ Author: 'Isaac Asimov' })).toEqual(['Isaac Asimov'])
    })

    it('extracts authors from array with name objects', () => {
      expect(
        extractAuthors({
          authors: [{ name: 'Frank Herbert' }, { Name: 'Stephen King' }],
        }),
      ).toEqual(['Frank Herbert', 'Stephen King'])
    })

    it('extracts authors from string array', () => {
      expect(
        extractAuthors({
          authors: ['Frank Herbert', 'Stephen King'],
        }),
      ).toEqual(['Frank Herbert', 'Stephen King'])
    })

    it('returns empty array when no authors found', () => {
      expect(extractAuthors({})).toEqual([])
      expect(extractAuthors({ author: '', authors: [] })).toEqual([])
    })

    it('prefers direct author over authors array', () => {
      expect(
        extractAuthors({
          author: 'Frank Herbert',
          authors: [{ name: 'Other Author' }],
        }),
      ).toEqual(['Frank Herbert'])
    })

    it('trims whitespace from author names', () => {
      expect(extractAuthors({ author: '  Frank Herbert  ' })).toEqual(['Frank Herbert'])
      expect(extractAuthors({ authors: ['  Author 1  ', '  Author 2  '] })).toEqual([
        'Author 1',
        'Author 2',
      ])
    })
  })

  describe('extractPublishedDate', () => {
    it('extracts date from publishedDate field', () => {
      expect(extractPublishedDate({ publishedDate: '2015-10-05' })).toBe('2015-10-05')
    })

    it('handles alternative date field names', () => {
      expect(extractPublishedDate({ releaseDate: '2015-10-05' })).toBe('2015-10-05')
      expect(extractPublishedDate({ ReleaseDate: '2015-10-05' })).toBe('2015-10-05')
      expect(extractPublishedDate({ release_date: '2015-10-05' })).toBe('2015-10-05')
    })

    it('converts Date object to ISO string', () => {
      const date = new Date('2015-10-05T00:00:00Z')
      const result = extractPublishedDate({ publishedDate: date })
      expect(result).toMatch(/2015-10-05/)
    })

    it('returns undefined when no date found', () => {
      expect(extractPublishedDate({})).toBeUndefined()
      expect(extractPublishedDate({ publishedDate: null })).toBeUndefined()
    })

    it('prioritizes first available date field', () => {
      expect(
        extractPublishedDate({
          publishedDate: '2015-10-05',
          releaseDate: '2016-01-01',
        }),
      ).toBe('2015-10-05')
    })
  })

  describe('extractNarrators', () => {
    it('extracts narrators from array with name objects', () => {
      expect(
        extractNarrators({
          narrators: [{ name: 'Scott Brick' }, { Name: 'Tim Curry' }],
        }),
      ).toBe('Scott Brick, Tim Curry')
    })

    it('extracts narrators from string array', () => {
      expect(extractNarrators({ narrators: ['Scott Brick', 'Tim Curry'] })).toBe(
        'Scott Brick, Tim Curry',
      )
    })

    it('handles single narrator string', () => {
      expect(extractNarrators({ narrator: 'Scott Brick' })).toBe('Scott Brick')
    })

    it('handles alternative field names', () => {
      expect(extractNarrators({ Narrators: ['Narrator 1'] })).toBe('Narrator 1')
      expect(extractNarrators({ Narrator: 'Single Narrator' })).toBe('Single Narrator')
    })

    it('returns empty string when no narrators', () => {
      expect(extractNarrators({})).toBe('')
      expect(extractNarrators({ narrators: [] })).toBe('')
    })

    it('trims whitespace from narrator names', () => {
      expect(extractNarrators({ narrators: ['  Scott Brick  ', '  Tim Curry  '] })).toBe(
        'Scott Brick, Tim Curry',
      )
    })
  })

  describe('normalizeRuntime', () => {
    it('assumes values >= 20000 are seconds', () => {
      expect(normalizeRuntime(130500)).toBe(2175) // 130500 / 60 = 2175 minutes
      expect(normalizeRuntime(45900)).toBe(765) // 45900 / 60 = 765
    })

    it('keeps values < 20000 as minutes', () => {
      expect(normalizeRuntime(60)).toBe(60)
      expect(normalizeRuntime(120)).toBe(120)
      expect(normalizeRuntime(3600)).toBe(3600) // 60 hours — valid for a very long audiobook
    })

    it('handles string input', () => {
      expect(normalizeRuntime('45900')).toBe(765)
      expect(normalizeRuntime('120')).toBe(120)
    })

    it('returns undefined for invalid input', () => {
      expect(normalizeRuntime(0)).toBeUndefined()
      expect(normalizeRuntime(-100)).toBeUndefined()
      expect(normalizeRuntime('invalid')).toBeUndefined()
      expect(normalizeRuntime(null)).toBeUndefined()
      expect(normalizeRuntime(undefined)).toBeUndefined()
    })

    it('rounds seconds-to-minutes conversion', () => {
      expect(normalizeRuntime(130559)).toBe(2176) // 130559 / 60 = 2175.98... -> 2176
    })
  })

  describe('processSeries', () => {
    it('processes series array with position', () => {
      const result = processSeries([
        { name: 'Dune', position: '1' },
        { name: 'Dune Messiah', position: '2' },
      ])
      expect(result.list).toEqual(['Dune #1', 'Dune Messiah #2'])
      expect(result.display).toBe('Dune #1')
    })

    it('handles series without position', () => {
      const result = processSeries([{ name: 'Series Name' }])
      expect(result.list).toEqual(['Series Name'])
      expect(result.display).toBe('Series Name')
    })

    it('handles string series array', () => {
      const result = processSeries(['Series 1', 'Series 2'])
      expect(result.list).toEqual(['Series 1', 'Series 2'])
      expect(result.display).toBe('Series 1')
    })

    it('returns empty when no series', () => {
      expect(processSeries([])).toEqual({ list: [], display: '' })
      expect(processSeries(null)).toEqual({ list: [], display: '' })
      expect(processSeries(undefined)).toEqual({ list: [], display: '' })
    })
  })

  describe('normalizeSource', () => {
    it('converts audimeta to Audible', () => {
      expect(normalizeSource('audimeta')).toBe('Audible')
      expect(normalizeSource('AUDIMETA')).toBe('Audible')
    })

    it('handles openlibrary', () => {
      expect(normalizeSource('openlibrary')).toBe('OpenLibrary')
      expect(normalizeSource('OpenLibrary')).toBe('OpenLibrary')
    })

    it('handles audible directly', () => {
      expect(normalizeSource('audible')).toBe('Audible')
      expect(normalizeSource('Audible')).toBe('Audible')
    })

    it('returns original for unknown source', () => {
      expect(normalizeSource('custom-source')).toBe('custom-source')
      expect(normalizeSource('NewSource')).toBe('NewSource')
    })

    it('returns empty for undefined', () => {
      expect(normalizeSource(undefined)).toBe('')
      expect(normalizeSource('')).toBe('')
    })
  })

  describe('isAudimetaSource', () => {
    it('detects audimeta by metadataSource', () => {
      expect(isAudimetaSource({ metadataSource: 'audimeta' })).toBe(true)
      expect(isAudimetaSource({ metadataSource: 'AUDIMETA' })).toBe(true)
    })

    it('detects audimeta by ASIN presence', () => {
      expect(isAudimetaSource({ asin: 'B000123456' })).toBe(true)
    })

    it('detects audimeta by isEnriched flag', () => {
      expect(isAudimetaSource({ isEnriched: true } as unknown as NormalizedResult)).toBe(true)
    })

    it('returns false for non-audimeta sources', () => {
      expect(isAudimetaSource({ metadataSource: 'openlibrary' })).toBe(false)
      expect(isAudimetaSource({})).toBe(false)
    })
  })

  describe('getPrimaryId', () => {
    it('prefers ASIN', () => {
      expect(getPrimaryId({ asin: 'B123', id: 'OL123', title: 'Book' })).toBe('B123')
    })

    it('falls back to id', () => {
      expect(getPrimaryId({ id: 'OL123', title: 'Book' })).toBe('OL123')
    })

    it('falls back to title', () => {
      expect(getPrimaryId({ title: 'My Book Title' })).toBe('My Book Title')
    })

    it('returns empty string when no identifier', () => {
      expect(getPrimaryId({})).toBe('')
    })
  })

  describe('extractSubtitle', () => {
    it('extracts subtitle', () => {
      expect(extractSubtitle({ subtitle: 'A Heroic Saga' })).toBe('A Heroic Saga')
      expect(extractSubtitle({ Subtitle: 'The Prequel' })).toBe('The Prequel')
    })

    it('handles subtitle array', () => {
      expect(extractSubtitle({ subtitles: ['Part 1', 'Part 2'] })).toBe('Part 1, Part 2')
    })

    it('returns undefined when no subtitle', () => {
      expect(extractSubtitle({})).toBeUndefined()
      expect(extractSubtitle({ subtitle: '' })).toBeUndefined()
    })
  })

  describe('extractDescription', () => {
    it('extracts description', () => {
      const desc = 'A long description of the book'
      expect(extractDescription({ description: desc })).toBe(desc)
      expect(extractDescription({ Description: desc })).toBe(desc)
    })

    it('returns undefined when no description', () => {
      expect(extractDescription({})).toBeUndefined()
    })
  })

  describe('extractPublishers', () => {
    it('extracts single publisher', () => {
      expect(extractPublishers({ publisher: 'Penguin Books' })).toEqual(['Penguin Books'])
    })

    it('extracts publisher array', () => {
      expect(extractPublishers({ publisher: ['Penguin', 'Random House'] })).toEqual([
        'Penguin',
        'Random House',
      ])
    })

    it('handles alternative field name', () => {
      expect(extractPublishers({ Publisher: 'Penguin' })).toEqual(['Penguin'])
    })

    it('returns empty array when no publisher', () => {
      expect(extractPublishers({})).toEqual([])
      expect(extractPublishers({ publisher: '' })).toEqual([])
    })

    it('trims whitespace', () => {
      expect(extractPublishers({ publisher: ['  Penguin  ', '  Random House  '] })).toEqual([
        'Penguin',
        'Random House',
      ])
    })
  })

  describe('extractLanguage', () => {
    it('extracts language', () => {
      expect(extractLanguage({ language: 'english' })).toBe('english')
      expect(extractLanguage({ Language: 'spanish' })).toBe('spanish')
      expect(extractLanguage({ locale: 'de-DE' })).toBe('de-DE')
    })

    it('returns undefined when no language', () => {
      expect(extractLanguage({})).toBeUndefined()
    })
  })

  describe('normalizeResultMetadata', () => {
    it('normalizes complete result', () => {
      const result: NormalizedResult = {
        asin: 'B123',
        title: 'Dune',
        author: 'Frank Herbert',
        narrators: [{ name: 'Scott Brick' }],
        publisher: 'Chilton',
        publishedDate: '1965-06-01',
        runtimeLengthMin: 900,
        language: 'english',
        series: [{ name: 'Dune', position: '1' }],
      }

      const normalized = normalizeResultMetadata(result)

      expect(normalized.authors).toEqual(['Frank Herbert'])
      expect(normalized.narrators).toBe('Scott Brick')
      expect(normalized.publishedDate).toBe('1965-06-01')
      expect(normalized.publishYear).toBe(1965)
      expect(normalized.runtime).toBe(900)
      expect(normalized.publishers).toEqual(['Chilton'])
      expect(normalized.language).toBe('english')
      expect(normalized.series.display).toBe('Dune #1')
      expect(normalized.primaryId).toBe('B123')
      expect(normalized.isAudimeta).toBe(true)
    })

    it('handles partial result', () => {
      const result: NormalizedResult = {
        title: 'Unknown Title',
      }

      const normalized = normalizeResultMetadata(result)

      expect(normalized.authors).toEqual([])
      expect(normalized.narrators).toBe('')
      expect(normalized.publishedDate).toBeUndefined()
      expect(normalized.runtime).toBeUndefined()
      expect(normalized.primaryId).toBe('Unknown Title')
      expect(normalized.isAudimeta).toBe(false)
    })
  })
})
