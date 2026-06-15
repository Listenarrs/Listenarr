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
import { describe, it, expect } from 'vitest'
import evaluateRules from '@/utils/customFilterEvaluator'
import type { Audiobook } from '@/types'

describe('customFilterEvaluator - grouping and precedence', () => {
  const base: Audiobook = {
    id: 1,
    title: 'Alpha Tales',
    authors: ['John Smith'],
    narrators: [],
    monitored: true,
    language: 'en',
    publisher: '',
    qualityProfileId: 0,
    publishYear: '2020',
    files: [],
    filePath: '',
    fileSize: 0,
  } as any as Audiobook

  it('evaluates simple AND/OR grouping: (A OR B) AND C', () => {
    const rules = [
      { field: 'title', operator: 'contains', value: 'alpha', groupStart: true },
      { field: 'title', operator: 'contains', value: 'beta', conjunction: 'or', groupEnd: true },
      { field: 'author', operator: 'contains', value: 'smith', conjunction: 'and' },
    ]

    // base has title Alpha and author Smith -> (true OR false) AND true => true
    expect(evaluateRules(base, rules)).toBe(true)

    // change base title so first two rules false
    const b2 = { ...base, title: 'Gamma' }
    expect(evaluateRules(b2 as Audiobook, rules)).toBe(false)
  })

  it('respects operator precedence (AND before OR) without parentheses', () => {
    // A OR B AND C should evaluate as A OR (B AND C)
    const rules = [
      { field: 'title', operator: 'contains', value: 'alpha' },
      { field: 'title', operator: 'contains', value: 'beta', conjunction: 'or' },
      { field: 'author', operator: 'contains', value: 'smith', conjunction: 'and' },
    ]

    // base: title contains alpha, so true OR (false AND true) => true
    expect(evaluateRules(base, rules)).toBe(true)

    // b3: title doesn't contain alpha, but contains beta and author smith -> false OR (true AND true) => true
    const b3 = { ...base, title: 'The Beta Story' }
    expect(evaluateRules(b3 as Audiobook, rules)).toBe(true)

    // b4: none match
    const b4 = { ...base, title: 'Gamma', authors: ['No One'] }
    expect(evaluateRules(b4 as Audiobook, rules)).toBe(false)
  })

  it('uses slim list file summary fields for path, filesize, and file count filters', () => {
    const slimBook = {
      ...base,
      files: undefined,
      fileCount: 2,
      filePath: '/library/Alpha Tales/book.m4b',
      fileSize: 5242880,
    } as Audiobook

    expect(
      evaluateRules(slimBook, [
        { field: 'path', operator: 'contains', value: '/library/alpha tales' },
        { field: 'files', operator: 'eq', value: '2', conjunction: 'and' },
        { field: 'filesize', operator: 'gt', value: '1048576', conjunction: 'and' },
      ]),
    ).toBe(true)
  })
})
