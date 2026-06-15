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
import {
  normalizePreferredSearchLanguage,
  normalizeSearchResultLanguage,
} from '@/utils/languageMapping'

describe('languageMapping', () => {
  it('normalizes supported languages and their aliases', () => {
    expect(normalizePreferredSearchLanguage('portuguese')).toBe('portuguese')
    expect(normalizePreferredSearchLanguage('pt')).toBe('portuguese')
    expect(normalizePreferredSearchLanguage('japanese')).toBe('japanese')
    expect(normalizePreferredSearchLanguage('ja')).toBe('japanese')
    expect(normalizePreferredSearchLanguage('hebrew')).toBe('hebrew')
    expect(normalizePreferredSearchLanguage('he')).toBe('hebrew')
    expect(normalizePreferredSearchLanguage('heb')).toBe('hebrew')
    expect(normalizePreferredSearchLanguage('iw')).toBe('hebrew')
    expect(normalizePreferredSearchLanguage('swedish')).toBe('swedish')
    expect(normalizePreferredSearchLanguage('sv')).toBe('swedish')
    expect(normalizePreferredSearchLanguage('swe')).toBe('swedish')
  })

  it('falls back safely for unsupported legacy preferred language values', () => {
    // Region codes that don't directly map to a supported language
    expect(normalizePreferredSearchLanguage('br')).toBe('portuguese')
    expect(normalizePreferredSearchLanguage('jp')).toBe('japanese')
  })

  it('maps result languages to supported filter values', () => {
    expect(normalizeSearchResultLanguage('portuguese')).toBe('portuguese')
    expect(normalizeSearchResultLanguage('japanese')).toBe('japanese')
    expect(normalizeSearchResultLanguage('swedish')).toBe('swedish')
    expect(normalizeSearchResultLanguage('br')).toBe('portuguese')
    expect(normalizeSearchResultLanguage('jp')).toBe('japanese')
  })

  it('returns undefined for truly unsupported result languages', () => {
    expect(normalizeSearchResultLanguage('klingon')).toBeUndefined()
    expect(normalizeSearchResultLanguage('')).toBeUndefined()
  })
})
