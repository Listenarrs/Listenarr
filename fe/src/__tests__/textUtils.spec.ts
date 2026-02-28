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

