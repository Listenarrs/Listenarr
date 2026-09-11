import { describe, it, expect } from 'vitest'
import { buildSeriesFields, formatSeriesMemberships, looksLikeAsin } from '@/utils/seriesUtils'

describe('formatSeriesMemberships', () => {
  it('lists every series a book belongs to with its number', () => {
    const result = formatSeriesMemberships({
      series: 'Publication Order',
      seriesNumber: '1',
      seriesMemberships: [
        { seriesName: 'Publication Order', seriesNumber: '1', isPrimary: true, sortOrder: 0 },
        { seriesName: 'Chronological Order', seriesNumber: '3', isPrimary: false, sortOrder: 1 },
      ],
    })
    expect(result).toBe('Publication Order #1, Chronological Order #3')
  })

  it('omits the number when a membership has none', () => {
    const result = formatSeriesMemberships({
      seriesMemberships: [{ seriesName: 'Standalone Saga', isPrimary: true, sortOrder: 0 }],
    })
    expect(result).toBe('Standalone Saga')
  })

  it('falls back to the legacy single series when there are no memberships', () => {
    expect(formatSeriesMemberships({ series: 'Solo Series', seriesNumber: '2' })).toBe(
      'Solo Series #2',
    )
    expect(formatSeriesMemberships({ series: 'No Number' })).toBe('No Number')
  })

  it('returns an empty string when there is no series information', () => {
    expect(formatSeriesMemberships({})).toBe('')
    expect(formatSeriesMemberships({ seriesMemberships: [] })).toBe('')
  })
})

describe('looksLikeAsin', () => {
  // The three below are real Audible series ASINs, for Ayesha, Allan Quatermain and the
  // She and Allan product record respectively.
  it.each(['B01E633FQM', 'B01F5TL5K4', 'B00CQ5WAXW'])('accepts the real ASIN %s', (asin) => {
    expect(looksLikeAsin(asin)).toBe(true)
  })

  it('accepts a lowercase ASIN and tolerates surrounding whitespace', () => {
    expect(looksLikeAsin('b01e633fqm')).toBe(true)
    expect(looksLikeAsin('  B01E633FQM  ')).toBe(true)
  })

  // These are series names, not identifiers, and each is exactly ten characters. A length-only
  // check lets all three through and the wizard then posts them as a seriesAsin.
  it.each(['Foundation', 'Bartimaeus', 'Peripheral'])(
    'rejects the ten-character series name %s',
    (name) => {
      expect(looksLikeAsin(name)).toBe(false)
    },
  )

  it('rejects values that are the wrong length or the wrong prefix', () => {
    expect(looksLikeAsin('B01E633FQ')).toBe(false)
    expect(looksLikeAsin('B01E633FQMX')).toBe(false)
    expect(looksLikeAsin('A01E633FQM')).toBe(false)
    expect(looksLikeAsin('B1E633FQMX')).toBe(false)
    expect(looksLikeAsin('0143122754')).toBe(false)
    expect(looksLikeAsin('')).toBe(false)
    expect(looksLikeAsin(undefined)).toBe(false)
    expect(looksLikeAsin(null)).toBe(false)
  })
})

describe('buildSeriesFields', () => {
  it('keeps an identifier that has the shape of an Audible ASIN', () => {
    const fields = buildSeriesFields([{ asin: 'B01E633FQM', name: 'Ayesha', position: '0' }])

    expect(fields.seriesMemberships?.[0].seriesAsin).toBe('B01E633FQM')
    expect(fields.seriesAsin).toBe('B01E633FQM')
  })

  it('drops a ten-character series name standing in for an identifier', () => {
    // The search fallback copies the series name into `asin` when the ASIN re-fetch fails.
    // Foundation is ten characters, so a length-only guard would store it as an identifier.
    const fields = buildSeriesFields([{ asin: 'Foundation', name: 'Foundation', position: '1' }])

    expect(fields.seriesMemberships).toEqual([
      {
        seriesName: 'Foundation',
        seriesNumber: '1',
        seriesAsin: undefined,
        isPrimary: true,
        sortOrder: 0,
      },
    ])
    expect(fields.seriesAsin).toBeUndefined()
    // The series itself is still kept; only the bogus identifier is dropped.
    expect(fields.series).toBe('Foundation')
  })
})
