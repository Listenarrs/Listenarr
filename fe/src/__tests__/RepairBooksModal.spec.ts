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
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { vi, describe, it, expect, beforeEach, afterEach } from 'vitest'

const apiMocks = vi.hoisted(() => ({
  getAudiobook: vi.fn(),
  getAudiobookIdentifiers: vi.fn(),
  advancedSearch: vi.fn(),
  updateAudiobookIdentifiers: vi.fn(),
  rescanAudiobookMetadata: vi.fn(),
  getApplicationSettings: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  apiService: apiMocks,
}))

import RepairBooksModal from '@/components/domain/repair/RepairBooksModal.vue'

// An English book matched to the French translation on a French instance
const mismatched = {
  id: 7,
  title: 'Primal Hunter, Vol. 7',
  authors: ['Zogarth'],
  asin: 'B0HM45F8MW',
}
const frenchAsin = {
  type: 'Asin',
  value: 'B0HM45F8MW',
  region: 'fr',
  isPrimary: true,
  source: 'Imported',
}
const isbn = {
  type: 'Isbn',
  value: '9791035410582',
  region: null,
  isPrimary: true,
  source: 'Imported',
}
const manualIsbn = {
  type: 'Isbn',
  value: '9781039400740',
  region: null,
  isPrimary: false,
  source: 'Manual',
}

const englishPh4 = {
  asin: 'B0BK9W82PQ',
  title: 'Primal Hunter 4',
  authors: [{ name: 'Zogarth' }],
  language: 'english',
  region: 'us',
}

const frenchVol7 = {
  asin: 'B0HM45F8MW',
  title: 'Primal Hunter, Vol. 7',
  authors: [{ name: 'Zogarth' }],
  language: 'french',
  region: 'fr',
}

// An Audible catalogue per region: ASIN lookups only find what that region sells
function catalogue(byRegion: Record<string, Array<Record<string, unknown>>>) {
  apiMocks.advancedSearch.mockImplementation(
    async ({ asin, region }: { asin?: string; region?: string }) => {
      const books = byRegion[region ?? ''] ?? []
      return asin ? books.filter((b) => b.asin === asin) : books
    },
  )
}

const field = (wrapper: ReturnType<typeof mountModal>, testid: string) =>
  row(wrapper).get(`[data-testid="${testid}"]`).element as HTMLInputElement | HTMLSelectElement

function mountModal(ids = [7]) {
  return mount(RepairBooksModal, {
    props: { visible: true, audiobookIds: ids },
    attachTo: document.body,
    global: { plugins: [createPinia()] },
  })
}

const row = (wrapper: ReturnType<typeof mountModal>, id = 7) =>
  wrapper.get(`[data-testid="repair-row-${id}"]`)

// Search the row's title and author in a region, then pick the result with that title
async function searchAndPick(
  wrapper: ReturnType<typeof mountModal>,
  { region = 'us', title = 'Primal Hunter 4' } = {},
) {
  await row(wrapper).get('[data-testid="match-region"]').setValue(region)
  // Changing the region looks the ASIN up there first
  await flushPromises()
  await row(wrapper).get('[data-testid="match-search"]').trigger('click')
  await flushPromises()
  const result = row(wrapper)
    .findAll('[data-testid="find-match-result"]')
    .find((r) => r.text().includes(title))
  await result!.trigger('click')
  await flushPromises()
}

async function repair(wrapper: ReturnType<typeof mountModal>) {
  await wrapper.get('[data-testid="repair-confirm"]').trigger('click')
  await flushPromises()
}

describe('RepairBooksModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    apiMocks.getApplicationSettings.mockResolvedValue({ defaultSearchRegion: 'fr' })
    apiMocks.getAudiobook.mockResolvedValue(mismatched)
    apiMocks.getAudiobookIdentifiers.mockResolvedValue({
      audiobookId: 7,
      identifiers: [frenchAsin, isbn, manualIsbn],
    })
    apiMocks.updateAudiobookIdentifiers.mockResolvedValue({
      message: 'ok',
      audiobook: { id: 7 },
      identifiers: [],
    })
    apiMocks.rescanAudiobookMetadata.mockResolvedValue({
      message: 'ok',
      audiobookId: 7,
      asin: 'B0BK9W82PQ',
      region: 'us',
    })
    catalogue({ us: [englishPh4], fr: [] })
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('opens with nothing to repair: each book shows its current record and no new one yet', async () => {
    const wrapper = mountModal()
    await flushPromises()

    expect(row(wrapper).text()).toContain('Primal Hunter, Vol. 7')
    expect(row(wrapper).get('[data-testid="match-current"]').text()).toMatch(
      /B0HM45F8MW\s+·\s+France \(FR\)/,
    )
    expect(row(wrapper).find('[data-testid="match-none"]').exists()).toBe(true)
    expect(row(wrapper).attributes('data-match-state')).toBe('ignored')
    expect(row(wrapper).get('[data-testid="repair-include"]').attributes('disabled')).toBeDefined()
    expect(field(wrapper, 'match-asin').value).toBe('B0HM45F8MW')
    expect(field(wrapper, 'match-region').value).toBe('fr')
    expect(wrapper.get('[data-testid="repair-confirm"]').text()).toContain('Repair 0')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe(
      'Find a new record for each book to repair',
    )
    expect(wrapper.get('[data-testid="match-legend"]').text()).toMatch(/Ignored.*Matched.*Error/)
  })

  it('counts only the books with a new record, ticked when found, and the skipped ones', async () => {
    const wrapper = mountModal([7, 8])
    await flushPromises()

    await searchAndPick(wrapper)

    expect(row(wrapper).attributes('data-match-state')).toBe('matched')
    expect(row(wrapper).get('[data-testid="repair-include"]').element).toHaveProperty(
      'checked',
      true,
    )
    expect(row(wrapper, 8).attributes('data-match-state')).toBe('ignored')
    expect(wrapper.get('[data-testid="repair-confirm"]').text()).toContain('Repair 1')
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe('1 skipped')

    // Unticked: ignored, though matched
    await row(wrapper).get('[data-testid="repair-include"]').setValue(false)
    expect(row(wrapper).attributes('data-match-state')).toBe('ignored')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('names each field above it, in the order ASIN, Title, Author, Region', async () => {
    const wrapper = mountModal()
    await flushPromises()

    expect(
      row(wrapper)
        .findAll('.match-field-label')
        .map((l) => l.text()),
    ).toEqual(['ASIN', 'Title', 'Author', 'Region'])
  })

  it('empties the ASIN when the title or author changes, and asks to pick the book again', async () => {
    for (const [testid, value] of [
      ['match-title', 'The Primal Hunter 4'],
      ['match-author', 'Someone else'],
    ]) {
      const wrapper = mountModal()
      await flushPromises()

      await row(wrapper).get(`[data-testid="${testid}"]`).setValue(value)

      expect(field(wrapper, 'match-asin').value).toBe('')
      expect(row(wrapper).get('[data-testid="match-asin-cleared"]').text()).toBe(
        `ASIN cleared: the ${testid === 'match-title' ? 'title' : 'author'} changed, search again.`,
      )
      expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
      wrapper.unmount()
    }
  })

  it('searches the title and author in the region and shows the results under the book', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()

    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Primal Hunter, Vol. 7', author: 'Zogarth', region: 'us' }),
      expect.any(AbortSignal),
    )
    const results = row(wrapper).findAll('[data-testid="find-match-result"]')
    expect(results.map((r) => r.text())).toEqual([expect.stringContaining('Primal Hunter 4')])
  })

  it('searches with Enter in the title or author, and Revert restores them', async () => {
    const wrapper = mountModal()
    await flushPromises()
    expect(field(wrapper, 'match-title').value).toBe('Primal Hunter, Vol. 7')
    expect(field(wrapper, 'match-author').value).toBe('Zogarth')

    await row(wrapper).get('[data-testid="match-title"]').setValue('The Primal Hunter 4')
    await row(wrapper).get('[data-testid="match-title"]').trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'The Primal Hunter 4', author: 'Zogarth' }),
      expect.any(AbortSignal),
    )
    await row(wrapper).get('[data-testid="match-revert"]').trigger('click')
    expect(field(wrapper, 'match-title').value).toBe('Primal Hunter, Vol. 7')
    expect(row(wrapper).find('[data-testid="find-match-result"]').exists()).toBe(false)
  })

  it('shows the search pending, with a red Cancel search button instead of Search', async () => {
    let answer!: (results: unknown[]) => void
    apiMocks.advancedSearch.mockReturnValue(new Promise((resolve) => (answer = resolve)))
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-search-pending"]').text()).toContain(
      'Searching "Primal Hunter, Vol. 7" in France (FR)',
    )
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(false)
    expect(row(wrapper).get('[data-testid="match-cancel-search"]').classes()).toContain(
      'btn-danger',
    )
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()

    answer([])
    await flushPromises()
    expect(row(wrapper).get('[data-testid="match-search-empty"]').text()).toContain(
      'No results for "Primal Hunter, Vol. 7" by "Zogarth" in France (FR)',
    )
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(true)
  })

  it('cancels the search, back to the fields with nothing to report', async () => {
    apiMocks.advancedSearch.mockImplementation(
      (_params: unknown, signal?: AbortSignal) =>
        new Promise((_resolve, reject) =>
          signal?.addEventListener('abort', () => reject(new Error('aborted'))),
        ),
    )
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    await row(wrapper).get('[data-testid="match-cancel-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).find('[data-testid="match-search-pending"]').exists()).toBe(false)
    expect(row(wrapper).find('[data-testid="match-search-error"]').exists()).toBe(false)
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(true)
  })

  it('stops a search that takes too long, says so and offers to retry', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    apiMocks.advancedSearch.mockImplementation(
      (_params: unknown, signal?: AbortSignal) =>
        new Promise((_resolve, reject) =>
          signal?.addEventListener('abort', () => reject(new Error('aborted'))),
        ),
    )
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    await vi.advanceTimersByTimeAsync(30_000)
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-search-error"]').text()).toContain(
      'The search took longer than 30 seconds',
    )
    catalogue({ fr: [frenchVol7] })
    await row(wrapper).get('[data-testid="match-retry"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)
  })

  it('shows a failed search with its error, Search becoming Retry until a field changes', async () => {
    apiMocks.advancedSearch.mockRejectedValueOnce(new Error('API error: 502 Bad Gateway'))
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-search-error"]').text()).toContain(
      'The search failed: API error: 502 Bad Gateway',
    )
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(true)
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(false)

    await row(wrapper).get('[data-testid="match-author"]').setValue('Zogarth ')
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(false)
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(true)
  })

  it('turns Search into Retry when the search finds nothing', async () => {
    catalogue({ fr: [] })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).find('[data-testid="match-search-empty"]').exists()).toBe(true)
    catalogue({ fr: [frenchVol7] })
    await row(wrapper).get('[data-testid="match-retry"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(true)
  })

  it('offers Retry when the ASIN lookup fails, and Retry looks it up again', async () => {
    apiMocks.advancedSearch.mockRejectedValueOnce(new Error('API error: 502 Bad Gateway'))
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    expect(row(wrapper).get('[data-testid="match-asin-error"]').text()).toContain(
      'The search failed: API error: 502 Bad Gateway',
    )

    catalogue({ us: [{ ...frenchVol7, region: 'us' }] })
    await row(wrapper).get('[data-testid="match-retry"]').trigger('click')
    await flushPromises()
    expect(apiMocks.advancedSearch).toHaveBeenLastCalledWith(
      expect.objectContaining({ asin: 'B0HM45F8MW', region: 'us' }),
      expect.any(AbortSignal),
    )
    expect(row(wrapper).attributes('data-match-state')).toBe('matched')
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(true)
  })

  it('shows the current and new records under the fields', async () => {
    const wrapper = mountModal()
    await flushPromises()

    const html = row(wrapper).html()
    expect(html.indexOf('data-testid="match-current"')).toBeGreaterThan(
      html.indexOf('data-testid="match-region"'),
    )
  })

  it('searches one book at a time: the others wait for it', async () => {
    apiMocks.getAudiobook.mockImplementation(async (id: number) =>
      id === 7 ? mismatched : { ...mismatched, id: 8, title: 'Other book' },
    )
    let answer!: (results: unknown[]) => void
    apiMocks.advancedSearch.mockReturnValue(new Promise((resolve) => (answer = resolve)))
    const wrapper = mountModal([7, 8])
    await flushPromises()

    await row(wrapper, 7).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    const otherSearch = row(wrapper, 8).get('[data-testid="match-search"]')
    expect(otherSearch.attributes('disabled')).toBeDefined()
    expect(otherSearch.attributes('title')).toBe('Another book is being searched')
    // Changing a region searches: locked too, on every book
    for (const id of [7, 8])
      expect(
        row(wrapper, id).get('[data-testid="match-region"]').attributes('disabled'),
      ).toBeDefined()

    answer([])
    await flushPromises()
    expect(
      row(wrapper, 8).get('[data-testid="match-search"]').attributes('disabled'),
    ).toBeUndefined()
    for (const id of [7, 8])
      expect(
        row(wrapper, id).get('[data-testid="match-region"]').attributes('disabled'),
      ).toBeUndefined()
  })

  it('takes the ASIN and region of the picked result, closes the results, then repairs with them', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await searchAndPick(wrapper)

    expect(row(wrapper).find('[data-testid="find-match-result"]').exists()).toBe(false)
    expect(field(wrapper, 'match-asin').value).toBe('B0BK9W82PQ')
    expect(field(wrapper, 'match-region').value).toBe('us')
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('Primal Hunter 4')

    apiMocks.advancedSearch.mockClear()
    await repair(wrapper)
    // A picked record is known to exist: no second lookup
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(
      7,
      expect.arrayContaining([
        expect.objectContaining({
          type: 'Asin',
          value: 'B0BK9W82PQ',
          region: 'us',
          isPrimary: true,
        }),
      ]),
    )
    expect(apiMocks.rescanAudiobookMetadata).toHaveBeenCalledWith(7)
    expect(wrapper.text()).toContain('B0BK9W82PQ · us')
  })

  it('saves the new ASIN as primary, drops the identifiers of the old match and keeps manual ones', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)

    await repair(wrapper)

    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(7, [
      { type: 'Asin', value: 'B0BK9W82PQ', region: 'us', isPrimary: true, source: 'Manual' },
      { type: 'Isbn', value: '9781039400740', region: null, isPrimary: false, source: 'Manual' },
    ])
  })

  it('keeps the ASIN when the region changes and looks it up there, with Cancel search', async () => {
    let answer: (results: unknown[]) => void = () => {}
    apiMocks.advancedSearch.mockImplementation(() => new Promise((resolve) => (answer = resolve)))
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('de')

    expect(field(wrapper, 'match-asin').value).toBe('B0HM45F8MW')
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ asin: 'B0HM45F8MW', region: 'de' }),
      expect.any(AbortSignal),
    )
    expect(row(wrapper).get('[data-testid="match-search-pending"]').text()).toContain(
      'Checking B0HM45F8MW in Germany (DE)',
    )
    expect(row(wrapper).find('[data-testid="match-cancel-search"]').exists()).toBe(true)
    answer([])
    await flushPromises()
  })

  it('selects the one record the region has for the ASIN, and fills the title and author with it', async () => {
    catalogue({
      de: [{ ...frenchVol7, title: 'Primal Hunter 7', authors: [{ name: 'Zogarth (DE)' }] }],
    })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('de')
    await flushPromises()

    const results = row(wrapper).findAll('[data-testid="find-match-result"]')
    expect(results).toHaveLength(1)
    expect(results[0]!.classes()).toContain('selected')
    expect(field(wrapper, 'match-title').value).toBe('Primal Hunter 7')
    expect(field(wrapper, 'match-author').value).toBe('Zogarth (DE)')
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('Primal Hunter 7')

    apiMocks.advancedSearch.mockClear()
    await repair(wrapper)
    // Already looked up: saved without a second lookup
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(
      7,
      expect.arrayContaining([expect.objectContaining({ value: 'B0HM45F8MW', region: 'de' })]),
    )
  })

  it('lets the user pick when the region has several records for the ASIN', async () => {
    catalogue({ de: [frenchVol7, { ...frenchVol7, title: 'Primal Hunter 7 (Dramatized)' }] })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('de')
    await flushPromises()

    const results = row(wrapper).findAll('[data-testid="find-match-result"]')
    expect(results).toHaveLength(2)
    expect(results.some((r) => r.classes().includes('selected'))).toBe(false)
    expect(field(wrapper, 'match-title').value).toBe('Primal Hunter, Vol. 7')
  })

  it('shows an error when the region has no record for the ASIN, and the book cannot be repaired', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()

    expect(field(wrapper, 'match-asin').value).toBe('B0HM45F8MW')
    expect(row(wrapper).get('[data-testid="match-asin-error"]').text()).toContain(
      'No record for B0HM45F8MW in Audible United States (US)',
    )
    expect(row(wrapper).attributes('data-match-state')).toBe('error')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()

    // Another ASIN clears the error, and is looked up once entered
    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9W82PQ')
    await flushPromises()
    expect(row(wrapper).find('[data-testid="match-asin-error"]').exists()).toBe(false)
    expect(row(wrapper).attributes('data-match-state')).toBe('matched')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeUndefined()
  })

  it('looks a typed ASIN up in its region once entered, then saves it', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    await row(wrapper).get('[data-testid="match-asin"]').setValue('b0bk9w82pq')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenLastCalledWith(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us', cap: 5 }),
      expect.any(AbortSignal),
    )
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('Primal Hunter 4')

    apiMocks.advancedSearch.mockClear()
    await repair(wrapper)
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(
      7,
      expect.arrayContaining([expect.objectContaining({ value: 'B0BK9W82PQ', region: 'us' })]),
    )
  })

  it('does not let a typed ASIN its region has no record for be repaired', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9W82PQ')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-asin-error"]').text()).toContain(
      'No record for B0BK9W82PQ in Audible France (FR)',
    )
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('forgets the new record while the ASIN is typed, and looks it up again once entered', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)
    const asin = row(wrapper).get('[data-testid="match-asin"]')

    ;(asin.element as HTMLInputElement).value = 'B0BK9W82PQ'
    await asin.trigger('input')
    expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(false)
    expect(row(wrapper).attributes('data-match-state')).toBe('ignored')

    await asin.trigger('change')
    await flushPromises()
    expect(apiMocks.advancedSearch).toHaveBeenLastCalledWith(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' }),
      expect.any(AbortSignal),
    )
    expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(true)
  })

  it('flags a malformed ASIN and does not let the repair start', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9')

    expect(row(wrapper).find('[data-testid="match-invalid-asin"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('reverts a book to its ASIN and region before the changes', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)

    await row(wrapper).get('[data-testid="match-revert"]').trigger('click')

    expect(field(wrapper, 'match-asin').value).toBe('B0HM45F8MW')
    expect(field(wrapper, 'match-region').value).toBe('fr')
    expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(false)
    expect(row(wrapper).get('[data-testid="match-revert"]').attributes('disabled')).toBeDefined()
  })

  it("offers every region: the book's current one, then the configured one and US, then the rest", async () => {
    apiMocks.getApplicationSettings.mockResolvedValue({ defaultSearchRegion: 'de' })
    const wrapper = mountModal()
    await flushPromises()

    const options = row(wrapper)
      .findAll('[data-testid="match-region"] option')
      .map((o) => o.attributes('value'))

    expect(options.slice(0, 3)).toEqual(['fr', 'de', 'us'])
    expect(options.length).toBeGreaterThan(3)
  })

  it('only rescans when the record found is the one the book already points at', async () => {
    catalogue({ fr: [frenchVol7] })
    const wrapper = mountModal()
    await flushPromises()
    const asin = row(wrapper).get('[data-testid="match-asin"]')
    await asin.trigger('change')
    await flushPromises()

    await repair(wrapper)

    expect(apiMocks.updateAudiobookIdentifiers).not.toHaveBeenCalled()
    expect(apiMocks.rescanAudiobookMetadata).toHaveBeenCalledWith(7)
  })

  it('retries the rescan once when it is refused for coming too soon', async () => {
    apiMocks.rescanAudiobookMetadata
      .mockRejectedValueOnce(Object.assign(new Error('Too many requests'), { status: 429 }))
      .mockResolvedValueOnce({ message: 'ok', audiobookId: 7, asin: 'B0HM45F8MW', region: 'fr' })
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)
    vi.useFakeTimers()

    const done = repair(wrapper)
    await vi.advanceTimersByTimeAsync(3000)
    await done
    await flushPromises()

    expect(apiMocks.rescanAudiobookMetadata).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('B0HM45F8MW · fr')
  })

  it('shows how long each repair took, in seconds with up to 2 decimals', async () => {
    // The clock moves only while the book is rescanned
    let now = 1000
    vi.spyOn(performance, 'now').mockImplementation(() => now)
    apiMocks.rescanAudiobookMetadata.mockImplementationOnce(async () => {
      now += 1234.5
      return { message: 'ok', audiobookId: 7, asin: 'B0HM45F8MW', region: 'fr' }
    })
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)

    await repair(wrapper)

    expect(wrapper.get('[data-testid="repair-result-duration"]').text()).toBe('1.23 s')
  })

  it('shows how long a failed repair took too, without trailing zeros', async () => {
    let now = 1000
    vi.spyOn(performance, 'now').mockImplementation(() => now)
    apiMocks.rescanAudiobookMetadata.mockImplementationOnce(async () => {
      now += 500
      throw new Error('Audible is down')
    })
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)

    await repair(wrapper)

    expect(wrapper.text()).toContain('Audible is down')
    expect(wrapper.get('[data-testid="repair-result-duration"]').text()).toBe('0.5 s')
  })

  it('offers to organize the repaired books', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)

    await repair(wrapper)
    await wrapper.get('[data-testid="repair-organize"]').trigger('click')

    expect(wrapper.emitted('organize')).toEqual([[[7]]])
  })
})
