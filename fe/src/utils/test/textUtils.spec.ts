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
import { decodeHtmlEntities, stripHtmlAndNormalize } from '@/utils/textUtils'

describe('textUtils', () => {
  it('decodes common named HTML entities', () => {
    expect(decodeHtmlEntities('Tom &amp; Jerry &quot;Test&quot;')).toBe('Tom & Jerry "Test"')
    expect(decodeHtmlEntities('Rock&nbsp;&amp;&nbsp;Roll')).toBe('Rock & Roll')
  })

  it('decodes numeric HTML entities', () => {
    expect(decodeHtmlEntities('&#39;Hello&#39;')).toBe("'Hello'")
    expect(decodeHtmlEntities('&#x41;&#x42;&#x43;')).toBe('ABC')
  })

  it('leaves unknown entities unchanged', () => {
    expect(decodeHtmlEntities('Hello &notarealentity;')).toBe('Hello &notarealentity;')
  })

  it('strips html and normalizes whitespace', () => {
    expect(stripHtmlAndNormalize('<p>Hello&nbsp;<strong>world</strong></p><br>Next')).toBe(
      'Hello world\n\nNext',
    )
  })
})
