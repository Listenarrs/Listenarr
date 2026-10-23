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
  getAudibleMetadata: vi.fn(),
  updateAudiobookIdentifiers: vi.fn(),
  rescanAudiobookMetadata: vi.fn(),
  getApplicationSettings: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  apiService: apiMocks,
}))

import RepairBooksModal from '@/components/domain/repair/RepairBooksModal.vue'
import { resultsReveal } from '@/utils/resultsReveal'

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

// An Audible catalogue per region, answering like the API does for Add New's search: titles
// through the search endpoint, an ASIN through the metadata endpoint (404 when the region doesn't
// sell it)
function catalogue(byRegion: Record<string, Array<Record<string, unknown>>>) {
  apiMocks.advancedSearch.mockImplementation(
    async ({ asin, region }: { asin?: string; region?: string }) => {
      const books = byRegion[region ?? ''] ?? []
      return asin ? books.filter((b) => b.asin === asin) : books
    },
  )
  apiMocks.getAudibleMetadata.mockImplementation(async (asin: string, region: string) => {
    const book = (byRegion[region] ?? []).find((b) => b.asin === asin)
    if (!book)
      throw Object.assign(new Error(`No metadata found for ASIN: ${asin}`), { status: 404 })
    return { metadata: book, source: 'audible' }
  })
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

// A book on the results step, once repaired
const result = (wrapper: ReturnType<typeof mountModal>, id = 7) =>
  wrapper.get(`[data-testid="repair-result-${id}"]`)

// Search the row's title and author in a region, then pick the result with that title
// A filled ASIN has priority: emptying it lets Search look for the title and author
async function emptyAsin(wrapper: ReturnType<typeof mountModal>, id = 7) {
  await row(wrapper, id).get('[data-testid="match-asin"]').setValue('')
}

async function searchAndPick(
  wrapper: ReturnType<typeof mountModal>,
  { region = 'us', title = 'Primal Hunter 4', id = 7, query = '' } = {},
) {
  await row(wrapper, id).get('[data-testid="match-region"]').setValue(region)
  // Changing the region searches there first (the ASIN, which is filled)
  await flushPromises()
  await emptyAsin(wrapper, id)
  if (query) await row(wrapper, id).get('[data-testid="match-title"]').setValue(query)
  await row(wrapper, id).get('[data-testid="match-search"]').trigger('click')
  await flushPromises()
  const result = row(wrapper, id)
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

    // The title says the step, and how many there are
    expect(wrapper.get('#modal-title h2').text()).toBe('Repair Books - Matching - Step 1/3')
    expect(row(wrapper).text()).toContain('Primal Hunter, Vol. 7')
    expect(row(wrapper).get('[data-testid="match-current"]').text()).toMatch(
      /^B0HM45F8MW - Primal Hunter, Vol. 7 - Zogarth - France \(FR\)$/,
    )
    expect(row(wrapper).find('[data-testid="match-none"]').exists()).toBe(true)
    expect(row(wrapper).attributes('data-match-state')).toBe('ignored')
    expect(row(wrapper).get('[data-testid="repair-include"]').attributes('disabled')).toBeDefined()
    expect(field(wrapper, 'match-asin').value).toBe('B0HM45F8MW')
    expect(field(wrapper, 'match-region').value).toBe('fr')
    expect(wrapper.get('[data-testid="repair-confirm"]').text()).toContain('Repair 0')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="repair-info-asin"]').text()).toBe(
      'A filled ASIN is searched on its own: the title and author are left out of that search.',
    )
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe('1 skipped')
    expect(wrapper.get('[data-testid="match-legend"]').text()).toMatch(
      /Ignored.*Matched.*Invalid.*Error.*Searching/,
    )
  })

  it('keeps the legend in the footer, between Cancel and the skipped count', async () => {
    const wrapper = mountModal([7, 8])
    await flushPromises()

    const footer = wrapper.get('.repair-footer').element
    const order = [
      '.cancel-button',
      '[data-testid="match-legend"]',
      '[data-testid="repair-count"]',
    ].map((selector) => footer.querySelector(selector)!)
    expect(order[0]!.compareDocumentPosition(order[1]!)).toBe(Node.DOCUMENT_POSITION_FOLLOWING)
    expect(order[1]!.compareDocumentPosition(order[2]!)).toBe(Node.DOCUMENT_POSITION_FOLLOWING)
    expect(wrapper.find('.repair-legend-above').exists()).toBe(false)
  })

  describe('with too little room for the legend on the footer line', () => {
    // jsdom has no layout: every element is 100px wide, the footer has room for `room` pixels, and
    // the observer measures as soon as it observes, as a browser does
    function layout(room: number) {
      vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(100)
      vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (
        this: HTMLElement,
      ) {
        return this.classList.contains('repair-footer') ? room : 100
      })
      vi.stubGlobal(
        'ResizeObserver',
        class {
          constructor(private readonly callback: () => void) {}
          observe() {
            this.callback()
          }
          disconnect() {}
        },
      )
    }

    afterEach(() => vi.unstubAllGlobals())

    it('puts the legend on its own line above Cancel, the skipped count and Repair', async () => {
      // Cancel, the five legend entries and the actions (count and Repair): 700px, more than the
      // footer has
      layout(500)
      const wrapper = mountModal()
      await flushPromises()

      expect(wrapper.get('.repair-footer').classes()).toContain('repair-footer-legend-above')
      expect(wrapper.get('[data-testid="match-legend"]').classes()).toContain('repair-legend-above')
      // The same lack of room: the book's fields take the narrow layout already
      expect(row(wrapper).classes()).toContain('match-card-narrow')
      // Cancel and the actions (200px) still fit: the count stays on one line
      expect(wrapper.get('[data-testid="repair-count"]').classes()).not.toContain(
        'repair-count-wraps',
      )
    })

    it('wraps the skipped count only when even Cancel, it and Repair do not fit one line', async () => {
      layout(150)
      const wrapper = mountModal()
      await flushPromises()

      expect(wrapper.get('[data-testid="match-legend"]').classes()).toContain('repair-legend-above')
      expect(wrapper.get('[data-testid="repair-count"]').classes()).toContain('repair-count-wraps')
    })

    it('leaves it between Cancel and the skipped count when the line has room', async () => {
      layout(2000)
      const wrapper = mountModal()
      await flushPromises()

      expect(wrapper.get('.repair-footer').classes()).not.toContain('repair-footer-legend-above')
      expect(wrapper.find('.repair-legend-above').exists()).toBe(false)
      expect(row(wrapper).classes()).not.toContain('match-card-narrow')
    })
  })

  it('shows the skipped count and the legend from the start, and the count at 0 too', async () => {
    const wrapper = mountModal()
    // Still loading: the footer already has its final shape
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe('0 skipped')
    expect(wrapper.find('[data-testid="match-legend"]').exists()).toBe(true)
    await flushPromises()
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe('1 skipped')

    // Every book repaired: none skipped, and the count says so
    await searchAndPick(wrapper)
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe('0 skipped')
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

  it('only prepares the next search while typing: the ASIN, results and record stay', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)
    apiMocks.advancedSearch.mockClear()
    apiMocks.getAudibleMetadata.mockClear()

    for (const testid of ['match-title', 'match-author', 'match-asin'])
      await row(wrapper).get(`[data-testid="${testid}"]`).setValue('Something else')

    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(apiMocks.getAudibleMetadata).not.toHaveBeenCalled()
    expect(row(wrapper).findAll('[data-testid="find-match-result"]').length).toBeGreaterThan(0)
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('B0BK9W82PQ')
    expect(row(wrapper).attributes('data-match-state')).toBe('matched')
    expect(row(wrapper).find('.match-hint').exists()).toBe(false)
  })

  it('repairs with the picked record, even after the ASIN field changed', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)
    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0SOMETHIN')

    await repair(wrapper)

    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(
      7,
      expect.arrayContaining([expect.objectContaining({ value: 'B0BK9W82PQ', region: 'us' })]),
    )
  })

  it('searches the title and author in the region and shows the results under the book', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    // Add New's title search: the region, up to 50 results, every language of its catalogue
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Primal Hunter, Vol. 7',
        author: 'Zogarth',
        region: 'us',
        pagination: { page: 1, limit: 50 },
      }),
    )
    expect(apiMocks.advancedSearch.mock.calls[0]![0]).not.toHaveProperty('language')
    const results = row(wrapper).findAll('[data-testid="find-match-result"]')
    expect(results.map((r) => r.text())).toEqual([expect.stringContaining('Primal Hunter 4')])
  })

  it('searches with Enter in the title or author, and Revert restores them', async () => {
    const wrapper = mountModal()
    await flushPromises()
    expect(field(wrapper, 'match-title').value).toBe('Primal Hunter, Vol. 7')
    expect(field(wrapper, 'match-author').value).toBe('Zogarth')

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-title"]').setValue('The Primal Hunter 4')
    await row(wrapper).get('[data-testid="match-title"]').trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'The Primal Hunter 4', author: 'Zogarth' }),
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

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-search-pending"]').text()).toContain(
      'Searching "Primal Hunter, Vol. 7" by "Zogarth" in France (FR)',
    )
    // Shown by the breathing blue bottom bar only: no line under the book, nothing moves
    expect(row(wrapper).attributes('data-match-state')).toBe('searching')
    expect(row(wrapper).find('.match-search-state').exists()).toBe(false)
    expect(row(wrapper).find('.match-bar').attributes('title')).toContain('Searching')
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(false)
    expect(row(wrapper).get('[data-testid="match-cancel-search"]').classes()).toContain(
      'btn-danger',
    )
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()

    answer([])
    await flushPromises()
    expect(row(wrapper).get('[data-testid="match-search-empty"]').text()).toMatch(
      /^No results for "Primal Hunter, Vol\. 7" by "Zogarth" in France \(FR\) - Request: [\d.]+ s$/,
    )
    // Nothing found is orange, not a failure
    expect(row(wrapper).attributes('data-match-state')).toBe('warning')
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(true)
  })

  it('heads the results with what was searched, which typing afterwards does not change', async () => {
    catalogue({ fr: [frenchVol7] })
    const wrapper = mountModal()
    await flushPromises()

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    const header = () => row(wrapper).get('[data-testid="match-results-header"]').text()
    expect(header()).toMatch(
      /^Results for "Primal Hunter, Vol\. 7" by "Zogarth" in France \(FR\) - Request: [\d.]+ s$/,
    )

    await row(wrapper).get('[data-testid="match-title"]').setValue('Something else')
    expect(header()).toMatch(
      /^Results for "Primal Hunter, Vol\. 7" by "Zogarth" in France \(FR\) - Request: [\d.]+ s$/,
    )
  })

  it('empties the ASIN field with its erase button, shown while the field holds something', async () => {
    const wrapper = mountModal()
    await flushPromises()
    const clear = row(wrapper).get('[data-testid="match-asin-clear"]')
    expect(clear.attributes('aria-label')).toBe('Empty the ASIN field')

    await clear.trigger('click')

    expect(field(wrapper, 'match-asin').value).toBe('')
    expect(row(wrapper).find('[data-testid="match-asin-clear"]').exists()).toBe(false)
    // Nothing searched by itself
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Primal Hunter, Vol. 7', author: 'Zogarth' }),
    )
  })

  it('empties the title and author with their erase buttons, without searching', async () => {
    const wrapper = mountModal()
    await flushPromises()

    for (const [testid, label] of [
      ['match-title', 'Title'],
      ['match-author', 'Author'],
    ]) {
      const clear = row(wrapper).get(`[data-testid="${testid}-clear"]`)
      expect(clear.attributes('aria-label')).toBe(`Empty the ${label} field`)
      await clear.trigger('click')
      expect(field(wrapper, testid!).value).toBe('')
      expect(row(wrapper).find(`[data-testid="${testid}-clear"]`).exists()).toBe(false)
    }
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(apiMocks.getAudibleMetadata).not.toHaveBeenCalled()
  })

  it('searches with Enter in the title like Search: a filled ASIN first', async () => {
    const wrapper = mountModal()
    await flushPromises()
    apiMocks.advancedSearch.mockClear()

    await row(wrapper).get('[data-testid="match-title"]').trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(apiMocks.getAudibleMetadata).toHaveBeenLastCalledWith('B0HM45F8MW', 'fr')
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
  })

  it('heads the results of a filled ASIN with the ASIN, which has priority over the title', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()

    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9W82PQ')
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-results-header"]').text()).toMatch(
      /^Results for ASIN "B0BK9W82PQ" in United States \(US\) - Request: [\d.]+ s$/,
    )
  })

  it('keeps the results while a new search runs; Cancel search empties them and says so', async () => {
    catalogue({ fr: [frenchVol7] })
    const wrapper = mountModal()
    await flushPromises()
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)
    await row(wrapper).get('[data-testid="find-match-result"]').trigger('click')
    expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(true)

    // A search that never answers: only Cancel search or the timeout ends it
    apiMocks.advancedSearch.mockImplementation(() => new Promise(() => {}))
    await row(wrapper).get('[data-testid="match-title"]').trigger('keydown', { key: 'Enter' })
    await flushPromises()
    // The previous results stay until the new search answers, nothing selected in them, New reset
    expect(row(wrapper).attributes('data-match-state')).toBe('searching')
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)
    expect(row(wrapper).find('[data-testid="find-match-result"].selected').exists()).toBe(false)
    expect(row(wrapper).get('[data-testid="match-none"]').exists()).toBe(true)

    await row(wrapper).get('[data-testid="match-cancel-search"]').trigger('click')
    await flushPromises()

    // The previous results don't answer the search canceled: the empty list in their place, the
    // header line naming the search canceled, and why in the middle of the list
    const header = row(wrapper).get('[data-testid="match-search-error"]')
    expect(header.text()).toBe('No results for "Primal Hunter, Vol. 7" by "Zogarth" in France (FR)')
    expect(header.classes()).toContain('match-search-warning')
    const reason = row(wrapper).get('[data-testid="match-results-reason"]')
    expect(reason.text()).toBe('Search canceled')
    expect(reason.classes()).toContain('match-search-warning')
    expect(row(wrapper).attributes('data-match-state')).toBe('warning')
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(0)
    expect(row(wrapper).find('[data-testid="match-results-placeholder"]').exists()).toBe(true)
    expect(row(wrapper).find('[data-testid="match-search-pending"]').exists()).toBe(false)
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(true)
  })

  it('stops a search that takes too long, says so and offers to retry', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    // A search that never answers: only Cancel search or the timeout ends it
    apiMocks.advancedSearch.mockImplementation(() => new Promise(() => {}))
    const wrapper = mountModal()
    await flushPromises()
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    await vi.advanceTimersByTimeAsync(30_000)
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-results-reason"]').text()).toContain(
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

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-results-reason"]').text()).toContain(
      'The search failed: API error: 502 Bad Gateway',
    )
    // A failed request is red
    expect(row(wrapper).attributes('data-match-state')).toBe('error')
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(true)
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(false)

    // Another author: a different search, Search again
    await row(wrapper).get('[data-testid="match-author"]').setValue('Zogarth Junior')
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(false)
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(true)
    // Changed back to what was searched: still Search, a field was changed
    await row(wrapper).get('[data-testid="match-author"]').setValue('Zogarth')
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(true)
  })

  it('turns Search into Retry when the search finds nothing', async () => {
    catalogue({ fr: [] })
    const wrapper = mountModal()
    await flushPromises()

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).find('[data-testid="match-search-empty"]').exists()).toBe(true)
    catalogue({ fr: [frenchVol7] })
    await row(wrapper).get('[data-testid="match-retry"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)
    // Found this time: still Retry, the fields unchanged
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(true)
  })

  it('serves a search already answered while the dialog is open without a request; Retry asks again', async () => {
    const wrapper = mountModal()
    await flushPromises()
    const region = (value: string) =>
      row(wrapper).get('[data-testid="match-region"]').setValue(value).then(flushPromises)

    await region('us')
    await region('fr')
    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledTimes(2)

    // Back to the US: already answered, no request, the same answer
    await region('us')
    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledTimes(2)
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(0)
    // The answer says it comes from the dialog's answered searches: maybe stale, Retry to check
    expect(row(wrapper).get('[data-testid="match-search-empty"]').text()).toBe(
      'No results for ASIN "B0HM45F8MW" in United States (US) - Cached results',
    )
    expect(row(wrapper).get('[data-testid="match-results-reason"]').text()).toBe(
      'No record for B0HM45F8MW in Audible United States (US)',
    )

    // Retry is the way to ask Audible again
    await row(wrapper).get('[data-testid="match-retry"]').trigger('click')
    await flushPromises()
    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledTimes(3)
  })

  it('shows Search once a field is changed, even back to what was searched, and serves it without a request', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(true)

    await row(wrapper).get('[data-testid="match-author"]').setValue('Someone')
    await row(wrapper).get('[data-testid="match-author"]').setValue('Zogarth')
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(true)

    apiMocks.advancedSearch.mockClear()
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(row(wrapper).get('[data-testid="match-results-header"]').text()).toMatch(
      / - Cached results$/,
    )
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(true)
  })

  it('says what was found, holds it, then fades out to the results, pickable once it is gone', async () => {
    resultsReveal.holdMs = 300
    resultsReveal.fadeMs = 200
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    try {
      const wrapper = mountModal()
      await flushPromises()
      await row(wrapper).get('[data-testid="match-region"]').setValue('us')
      await flushPromises()
      await emptyAsin(wrapper)
      const found = () => row(wrapper).find('[data-testid="match-results-found"]')
      const pickFirst = () => row(wrapper).get('[data-testid="find-match-result"]').trigger('click')

      // Asked Audible: the answer is said, over the results, which can't be picked yet
      await row(wrapper).get('[data-testid="match-search"]').trigger('click')
      await flushPromises()
      expect(found().text()).toBe('Found 1 result')
      expect(row(wrapper).get('[data-testid="match-results"]').classes()).toContain(
        'match-results-revealing',
      )
      await pickFirst()
      expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(false)

      // Readable for 300 ms, then a 200 ms fade: pickable after 500 ms
      await vi.advanceTimersByTimeAsync(499)
      expect(found().exists()).toBe(true)
      await vi.advanceTimersByTimeAsync(1)
      expect(found().exists()).toBe(false)
      await pickFirst()
      expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(true)

      // Served from the answered searches, at once: the same reveal
      await row(wrapper).get('[data-testid="match-author"]').setValue('Zogarth')
      await row(wrapper).get('[data-testid="match-search"]').trigger('click')
      await flushPromises()
      expect(found().text()).toBe('Found 1 result')
      await vi.advanceTimersByTimeAsync(500)
      expect(found().exists()).toBe(false)
    } finally {
      resultsReveal.holdMs = 0
      resultsReveal.fadeMs = 0
    }
  })

  it('keeps the room of the results from the start when the dialog repairs one book', async () => {
    catalogue({ fr: [] })
    const wrapper = mountModal()
    await flushPromises()

    // Before any search: the header line and the empty list, at their place
    expect(row(wrapper).get('[data-testid="match-results-header"]').text()).toBe(
      'No search request done',
    )
    expect(row(wrapper).find('[data-testid="match-results-placeholder"]').exists()).toBe(true)

    // The first search waits over the empty list, under the spinner
    let answer!: (results: unknown[]) => void
    apiMocks.advancedSearch.mockReturnValueOnce(new Promise((resolve) => (answer = resolve)))
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).find('[data-testid="match-results-pending"]').exists()).toBe(true)

    // Nothing found: said on the header line, over the empty list
    answer([])
    await flushPromises()
    expect(row(wrapper).get('[data-testid="match-search-empty"]').text()).toMatch(
      /^No results for "Primal Hunter, Vol\. 7" by "Zogarth" in France \(FR\) - Request: [\d.]+ s$/,
    )
    expect(row(wrapper).find('[data-testid="match-results-placeholder"]').exists()).toBe(true)
  })

  it('puts the empty list back when a search fails, and says why on its header line', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)

    apiMocks.advancedSearch.mockRejectedValueOnce(new Error('API error: 502 Bad Gateway'))
    await row(wrapper).get('[data-testid="match-retry"]').trigger('click')
    await flushPromises()

    // No results to show: the empty list, as before any search, and the reason in red in its
    // middle
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(0)
    const placeholder = row(wrapper).get('[data-testid="match-results-placeholder"]')
    const header = row(wrapper).get('[data-testid="match-search-error"]')
    expect(header.text()).toBe(
      'No results for "Primal Hunter, Vol. 7" by "Zogarth" in United States (US)',
    )
    expect(header.classes()).toContain('match-search-error')
    expect(header.classes()).toContain('match-results-header')
    const reason = placeholder.get('[data-testid="match-results-reason"]')
    expect(reason.text()).toBe('The search failed: API error: 502 Bad Gateway')
    expect(reason.classes()).toContain('match-search-error')
  })

  it('keeps the height of the results a canceled search empties, with several books', async () => {
    const wrapper = mountModal([7, 8])
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    // No room kept from the start with several books
    expect(row(wrapper).find('[data-testid="match-results-placeholder"]').exists()).toBe(false)

    // jsdom has no layout: the results list is 180px high
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(180)
    apiMocks.advancedSearch.mockImplementationOnce(() => new Promise(() => {}))
    await row(wrapper).get('[data-testid="match-retry"]').trigger('click')
    await flushPromises()
    await row(wrapper).get('[data-testid="match-cancel-search"]').trigger('click')
    await flushPromises()

    const placeholder = row(wrapper).get('[data-testid="match-results-placeholder"]')
    expect((placeholder.element as HTMLElement).style.height).toBe('180px')
  })

  it('gives a book of several an empty list one result high with its first search', async () => {
    const wrapper = mountModal([7, 8])
    await flushPromises()
    expect(row(wrapper).find('[data-testid="match-results-placeholder"]').exists()).toBe(false)

    // The first search waits over an empty list one result high, under the spinner
    let answer!: (results: unknown[]) => void
    apiMocks.advancedSearch.mockReturnValueOnce(new Promise((resolve) => (answer = resolve)))
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    const placeholder = row(wrapper).get('[data-testid="match-results-placeholder"]')
    expect(placeholder.classes()).toContain('match-results-empty-row')
    expect(row(wrapper).find('[data-testid="match-results-pending"]').exists()).toBe(true)

    // Nothing found: the empty list stays, the card doesn't shrink back
    answer([])
    await flushPromises()
    expect(row(wrapper).find('[data-testid="match-results-placeholder"]').exists()).toBe(true)
    expect(row(wrapper).find('[data-testid="match-search-empty"]').exists()).toBe(true)
  })

  it('says no search was done for every book, but keeps the list room for one book only', async () => {
    const wrapper = mountModal([7, 8])
    await flushPromises()

    for (const id of [7, 8]) {
      expect(row(wrapper, id).get('[data-testid="match-results-header"]').text()).toBe(
        'No search request done',
      )
      // Several books: the results open under each one when found
      expect(row(wrapper, id).find('[data-testid="match-results-placeholder"]').exists()).toBe(
        false,
      )
    }
  })

  it('never keeps a failed search: the same values ask again', async () => {
    apiMocks.getAudibleMetadata.mockRejectedValueOnce(new Error('API error: 502 Bad Gateway'))
    const wrapper = mountModal()
    await flushPromises()
    const region = (value: string) =>
      row(wrapper).get('[data-testid="match-region"]').setValue(value).then(flushPromises)

    await region('us')
    await region('fr')
    await region('us')
    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledTimes(3)
  })

  it('shares the answered searches between the books of the dialog', async () => {
    const wrapper = mountModal([7, 8])
    await flushPromises()
    // Both books have the same title, author and ASIN
    for (const id of [7, 8]) {
      await row(wrapper, id).get('[data-testid="match-region"]').setValue('us')
      await flushPromises()
      await emptyAsin(wrapper, id)
      await row(wrapper, id).get('[data-testid="match-search"]').trigger('click')
      await flushPromises()
    }

    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledTimes(1)
    expect(apiMocks.advancedSearch).toHaveBeenCalledTimes(1)
    expect(row(wrapper, 8).findAll('[data-testid="find-match-result"]').length).toBeGreaterThan(0)
    // The header says where they come from
    expect(row(wrapper).get('[data-testid="match-results-header"]').text()).toMatch(
      / - Request: [\d.]+ s$/,
    )
    expect(row(wrapper, 8).get('[data-testid="match-results-header"]').text()).toMatch(
      / - Cached results$/,
    )
  })

  it('asks Audible afresh each time the dialog opens', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledTimes(1)

    await wrapper.setProps({ visible: false })
    await wrapper.setProps({ visible: true })
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledTimes(2)
  })

  it('offers Retry when the ASIN lookup fails, and Retry looks it up again', async () => {
    apiMocks.getAudibleMetadata.mockRejectedValueOnce(new Error('API error: 502 Bad Gateway'))
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    expect(row(wrapper).get('[data-testid="match-results-reason"]').text()).toContain(
      'The search failed: API error: 502 Bad Gateway',
    )

    catalogue({ us: [{ ...frenchVol7, region: 'us' }] })
    await row(wrapper).get('[data-testid="match-retry"]').trigger('click')
    await flushPromises()
    expect(apiMocks.getAudibleMetadata).toHaveBeenLastCalledWith('B0HM45F8MW', 'us')
    // Found, offered to pick: nothing picked for the user
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)
    expect(row(wrapper).attributes('data-match-state')).toBe('ignored')
    expect(row(wrapper).find('[data-testid="match-retry"]').exists()).toBe(true)
  })

  it('sums the book up next to Revert: ASIN - Title: Subtitle - Author - Region', async () => {
    apiMocks.getAudiobook.mockResolvedValue({ ...mismatched, subtitle: 'A LitRPG Adventure' })
    const wrapper = mountModal()
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-summary"]').text()).toBe(
      'B0HM45F8MW - Primal Hunter, Vol. 7: A LitRPG Adventure - Zogarth - France (FR)',
    )
    // Current reads the same
    expect(row(wrapper).get('[data-testid="match-current"]').text()).toBe(
      row(wrapper).get('[data-testid="match-summary"]').text(),
    )
  })

  it('shows each result with its subtitle, where some books keep their volume number', async () => {
    catalogue({ us: [{ ...englishPh4, title: 'The Primal Hunter', subtitle: 'Book 4' }] })
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="find-match-result"] .result-title').text()).toBe(
      'The Primal Hunter: Book 4',
    )
  })

  it('sums New up like Current, every author included', async () => {
    catalogue({
      us: [
        {
          ...englishPh4,
          subtitle: 'A LitRPG Adventure',
          authors: [{ name: 'Zogarth' }, { name: 'Co Author' }],
        },
      ],
    })
    const wrapper = mountModal()
    await flushPromises()

    await searchAndPick(wrapper)

    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toMatch(
      /^B0BK9W82PQ - Primal Hunter 4: A LitRPG Adventure - Zogarth, Co Author - United States \(US\)/,
    )
  })

  it('leaves the parts a book does not have out of its summary', async () => {
    apiMocks.getAudiobook.mockResolvedValue({ id: 7, title: 'Untitled', authors: [] })
    apiMocks.getAudiobookIdentifiers.mockResolvedValue({ audiobookId: 7, identifiers: [] })
    const wrapper = mountModal()
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-summary"]').text()).toBe('Untitled')
  })

  it('tags the current book in the results, and turns only the picked one green', async () => {
    const otherVol7 = { ...frenchVol7, asin: 'B0FRVOL7XX', title: 'Primal Hunter, Vol. 7 (other)' }
    catalogue({ fr: [frenchVol7, otherVol7] })
    const wrapper = mountModal()
    await flushPromises()

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    const selected = () =>
      row(wrapper)
        .findAll('[data-testid="find-match-result"]')
        .filter((r) => r.classes().includes('selected'))
        .map((r) => r.text())
    const results = () => row(wrapper).findAll('[data-testid="find-match-result"]')
    // Nothing picked yet: no green row, the current record tagged
    expect(selected()).toEqual([])
    expect(results()[0]!.get('[data-testid="find-match-current"]').text()).toBe('Current')
    expect(results()[1]!.find('[data-testid="find-match-current"]').exists()).toBe(false)

    await results()[1]!.trigger('click')
    expect(selected()).toEqual([expect.stringContaining('B0FRVOL7XX')])
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('B0FRVOL7XX')
    expect(results()[0]!.find('[data-testid="find-match-current"]').exists()).toBe(true)

    // Picking the current record turns it green too
    await results()[0]!.trigger('click')
    expect(selected()).toEqual([expect.stringContaining('B0HM45F8MW')])
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('B0HM45F8MW')
  })

  it('shows the current and new records under the fields', async () => {
    const wrapper = mountModal()
    await flushPromises()

    const html = row(wrapper).html()
    expect(html.indexOf('data-testid="match-current"')).toBeGreaterThan(
      html.indexOf('data-testid="match-region"'),
    )
  })

  it('greys the previous results out under a spinner while a new search runs, and keeps them unpickable', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    const results = () => row(wrapper).get('[data-testid="match-results"]')
    expect(results().classes()).not.toContain('match-results-pending')
    expect(row(wrapper).find('[data-testid="match-results-pending"]').exists()).toBe(false)

    // Retry, a new request: the previous results stay, greyed out, under a spinning wheel
    let answer!: (results: unknown[]) => void
    apiMocks.advancedSearch.mockReturnValueOnce(new Promise((resolve) => (answer = resolve)))
    await row(wrapper).get('[data-testid="match-retry"]').trigger('click')
    await flushPromises()
    expect(results().classes()).toContain('match-results-pending')
    expect(results().attributes('aria-busy')).toBe('true')
    expect(row(wrapper).get('[data-testid="match-results-pending-text"]').text()).toBe(
      'Searching "Primal Hunter, Vol. 7" by "Zogarth" in United States (US)…',
    )

    // A stale result can't be picked
    await row(wrapper).get('[data-testid="find-match-result"]').trigger('click')
    expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(false)

    // The answer replaces them, pickable again
    answer([englishPh4])
    await flushPromises()
    expect(results().classes()).not.toContain('match-results-pending')
    expect(row(wrapper).find('[data-testid="match-results-pending"]').exists()).toBe(false)
    await row(wrapper).get('[data-testid="find-match-result"]').trigger('click')
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('Primal Hunter 4')
  })

  it("greys a book's results out too while another book is searched, and says why", async () => {
    apiMocks.getAudiobook.mockImplementation(async (id: number) =>
      id === 7 ? mismatched : { ...mismatched, id: 8, title: 'Other book' },
    )
    const wrapper = mountModal([7, 8])
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    // Book 8 is searched: book 7's results wait, greyed out, and can't be picked
    let answer!: (results: unknown[]) => void
    apiMocks.advancedSearch.mockReturnValueOnce(new Promise((resolve) => (answer = resolve)))
    await emptyAsin(wrapper, 8)
    await row(wrapper, 8).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).get('[data-testid="match-results"]').classes()).toContain(
      'match-results-pending',
    )
    expect(row(wrapper).get('[data-testid="match-results-pending-text"]').text()).toBe(
      'Waiting: a search is running on another book',
    )
    await row(wrapper).get('[data-testid="find-match-result"]').trigger('click')
    expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(false)

    answer([])
    await flushPromises()
    expect(row(wrapper).get('[data-testid="match-results"]').classes()).not.toContain(
      'match-results-pending',
    )
  })

  it('searches one book at a time: the other cards wait for it, locked', async () => {
    apiMocks.getAudiobook.mockImplementation(async (id: number) =>
      id === 7 ? mismatched : { ...mismatched, id: 8, title: 'Other book' },
    )
    let answer!: (results: unknown[]) => void
    apiMocks.advancedSearch.mockReturnValue(new Promise((resolve) => (answer = resolve)))
    const wrapper = mountModal([7, 8])
    await flushPromises()

    await emptyAsin(wrapper)
    await row(wrapper, 7).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    const otherSearch = row(wrapper, 8).get('[data-testid="match-search"]')
    expect(otherSearch.attributes('disabled')).toBeDefined()
    expect(otherSearch.attributes('title')).toBe('Another book is being searched')
    // The whole other card waits: its checkbox, fields and Revert can't be used either
    for (const control of row(wrapper, 8).findAll('input, select, button'))
      expect(control.element.matches(':disabled')).toBe(true)
    // Enter in one of its fields doesn't start a second search
    apiMocks.advancedSearch.mockClear()
    await row(wrapper, 8).get('[data-testid="match-title"]').trigger('keydown.enter')
    await flushPromises()
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
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
    expect(row(wrapper, 8).get('[data-testid="match-title"]').element.matches(':disabled')).toBe(
      false,
    )
    for (const id of [7, 8])
      expect(
        row(wrapper, id).get('[data-testid="match-region"]').attributes('disabled'),
      ).toBeUndefined()
  })

  it('takes the ASIN and region of the picked result, then repairs with them', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await searchAndPick(wrapper)

    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('B0BK9W82PQ')
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
    let answer: () => void = () => {}
    apiMocks.getAudibleMetadata.mockImplementation(
      () =>
        new Promise((_resolve, reject) => {
          answer = () => reject(Object.assign(new Error('Not found'), { status: 404 }))
        }),
    )
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('de')

    expect(field(wrapper, 'match-asin').value).toBe('B0HM45F8MW')
    // Add New's ASIN search: the metadata sources, in the region
    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('B0HM45F8MW', 'de')
    expect(row(wrapper).get('[data-testid="match-search-pending"]').text()).toContain(
      'Searching ASIN "B0HM45F8MW" in Germany (DE)',
    )
    expect(row(wrapper).find('[data-testid="match-cancel-search"]').exists()).toBe(true)
    answer()
    await flushPromises()
  })

  it('offers the one record the region has for the ASIN without picking it', async () => {
    catalogue({
      de: [{ ...frenchVol7, title: 'Primal Hunter 7', authors: [{ name: 'Zogarth (DE)' }] }],
    })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('de')
    await flushPromises()

    const results = row(wrapper).findAll('[data-testid="find-match-result"]')
    expect(results).toHaveLength(1)
    expect(results[0]!.classes()).not.toContain('selected')
    expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="repair-confirm"]').text()).toContain('Repair 0')

    // Picked by the user: the new record, the fields left as typed
    await results[0]!.trigger('click')
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('Primal Hunter 7')
    expect(field(wrapper, 'match-title').value).toBe('Primal Hunter, Vol. 7')
    expect(field(wrapper, 'match-author').value).toBe('Zogarth')

    apiMocks.advancedSearch.mockClear()
    await repair(wrapper)
    // Already looked up: saved without a second lookup
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(
      7,
      expect.arrayContaining([expect.objectContaining({ value: 'B0HM45F8MW', region: 'de' })]),
    )
  })

  it('shows an error when the region has no record for the ASIN, and the book cannot be repaired', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()

    expect(field(wrapper, 'match-asin').value).toBe('B0HM45F8MW')
    expect(row(wrapper).get('[data-testid="match-results-reason"]').text()).toContain(
      'No record for B0HM45F8MW in Audible United States (US)',
    )
    // New only shows a record picked in the results: no record, its default text
    expect(row(wrapper).get('[data-testid="match-none"]').text()).toBe(
      'No new record yet: search the book and pick it, or type its ASIN',
    )
    // No record is a search that found nothing: orange
    expect(row(wrapper).attributes('data-match-state')).toBe('warning')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()

    // Another ASIN clears the error, and Search looks it up
    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9W82PQ')
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).find('[data-testid="match-results-reason"]').exists()).toBe(false)
    await row(wrapper).get('[data-testid="find-match-result"]').trigger('click')
    expect(row(wrapper).attributes('data-match-state')).toBe('matched')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeUndefined()
  })

  it('looks a filled ASIN up before the title and author, and the title once the ASIN is emptied', async () => {
    catalogue({ fr: [frenchVol7, { ...englishPh4, region: 'fr' }] })
    const wrapper = mountModal()
    await flushPromises()
    apiMocks.advancedSearch.mockClear()

    // Filled from the start with the book's ASIN: Search looks it up, the title left out
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(apiMocks.getAudibleMetadata).toHaveBeenLastCalledWith('B0HM45F8MW', 'fr')
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(field(wrapper, 'match-title').value).toBe('Primal Hunter, Vol. 7')

    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9W82PQ')
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(apiMocks.getAudibleMetadata).toHaveBeenLastCalledWith('B0BK9W82PQ', 'fr')
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    // The record found for the ASIN left the title and author as they were
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Primal Hunter, Vol. 7', author: 'Zogarth' }),
    )
  })

  it('starts the search Search would with Enter in any field or a new region: ASIN first', async () => {
    const wrapper = mountModal()
    await flushPromises()
    const enter = (testid: string) =>
      row(wrapper).get(`[data-testid="${testid}"]`).trigger('keydown', { key: 'Enter' })
    const searched = () => ({
      asin: apiMocks.getAudibleMetadata.mock.calls.length,
      title: apiMocks.advancedSearch.mock.calls.length,
    })
    const reset = () => {
      apiMocks.getAudibleMetadata.mockClear()
      apiMocks.advancedSearch.mockClear()
    }

    // The ASIN is filled: Enter in the title or the author looks it up, on its own
    for (const testid of ['match-title', 'match-author']) {
      reset()
      await enter(testid)
      await flushPromises()
      expect(searched()).toEqual({ asin: 1, title: 0 })
    }

    // The ASIN is empty: Enter in it searches the title and author, as Search would
    await emptyAsin(wrapper)
    reset()
    await enter('match-asin')
    await flushPromises()
    expect(searched()).toEqual({ asin: 0, title: 1 })

    // And so does a new region, there
    reset()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    expect(searched()).toEqual({ asin: 0, title: 1 })
    expect(apiMocks.advancedSearch).toHaveBeenLastCalledWith(
      expect.objectContaining({ title: 'Primal Hunter, Vol. 7', region: 'us' }),
    )
  })

  it('looks a typed ASIN up in its region with Enter, then saves it', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()
    await row(wrapper).get('[data-testid="match-asin"]').setValue('b0bk9w82pq')
    await row(wrapper).get('[data-testid="match-asin"]').trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(apiMocks.getAudibleMetadata).toHaveBeenLastCalledWith('B0BK9W82PQ', 'us')
    await row(wrapper).get('[data-testid="find-match-result"]').trigger('click')
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('Primal Hunter 4')
    // The record picked leaves the title and author as typed
    expect(field(wrapper, 'match-title').value).toBe('Primal Hunter, Vol. 7')
    expect(field(wrapper, 'match-author').value).toBe('Zogarth')

    apiMocks.getAudibleMetadata.mockClear()
    await repair(wrapper)
    expect(apiMocks.getAudibleMetadata).not.toHaveBeenCalled()
    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(
      7,
      expect.arrayContaining([expect.objectContaining({ value: 'B0BK9W82PQ', region: 'us' })]),
    )
  })

  it('does not let a typed ASIN its region has no record for be repaired', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9W82PQ')
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-results-reason"]').text()).toContain(
      'No record for B0BK9W82PQ in Audible France (FR)',
    )
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('keeps the new record while an ASIN is typed, and a new search unpicks it', async () => {
    catalogue({ us: [englishPh4, { ...frenchVol7, asin: 'B0OTHER123', title: 'Other' }] })
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)
    const asin = row(wrapper).get('[data-testid="match-asin"]')

    await asin.setValue('B0OTHER123')
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('B0BK9W82PQ')

    await asin.trigger('keydown', { key: 'Enter' })
    await flushPromises()
    expect(apiMocks.getAudibleMetadata).toHaveBeenLastCalledWith('B0OTHER123', 'us')
    // The new search unpicked the previous record: New is back to no record
    expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(false)
    expect(row(wrapper).get('[data-testid="match-none"]').exists()).toBe(true)
    await row(wrapper).get('[data-testid="find-match-result"]').trigger('click')
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('B0OTHER123')
  })

  it('does not let a malformed ASIN be repaired, and says nothing about it before Search', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9')

    expect(row(wrapper).find('[data-testid="match-search-error"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('says nothing about an empty ASIN, and has nothing to search for it', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-asin"]').setValue('')

    expect(row(wrapper).find('[data-testid="match-search-error"]').exists()).toBe(false)
    expect(row(wrapper).find('.match-hint').exists()).toBe(false)
  })

  it('searches a malformed ASIN as a field error: its rules shown, orange, nothing sent', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9')
    apiMocks.getAudibleMetadata.mockClear()
    apiMocks.advancedSearch.mockClear()

    const search = row(wrapper).get('[data-testid="match-search"]')
    expect(search.attributes('disabled')).toBeUndefined()
    await search.trigger('click')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-search-error"]').text()).toBe(
      'No search request done',
    )
    const error = row(wrapper).get('[data-testid="match-results-reason"]')
    expect(error.text()).toContain('Not an ASIN: an ASIN is B followed by 9 letters or digits')
    expect(error.classes()).toContain('match-search-warning')
    expect(row(wrapper).attributes('data-match-state')).toBe('warning')
    expect(apiMocks.getAudibleMetadata).not.toHaveBeenCalled()
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()

    // Typing only prepares the next search: the message stays until it runs
    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9W82PQ')
    expect(row(wrapper).find('[data-testid="match-search-error"]').exists()).toBe(true)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).find('[data-testid="match-search-error"]').exists()).toBe(false)
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
    // Only a new search replaces the results
    expect(row(wrapper).findAll('[data-testid="find-match-result"]').length).toBeGreaterThan(0)
  })

  it("offers every region in canary's order: the book's, the preferred one and US, then by name", async () => {
    apiMocks.getApplicationSettings.mockResolvedValue({ defaultSearchRegion: 'de' })
    const wrapper = mountModal()
    await flushPromises()

    // Codes in the field, the full name as each option's tooltip; the rest by full name
    const shown = row(wrapper).findAll('[data-testid="match-region"] option')
    expect(shown.map((o) => o.text())).toEqual([
      'FR',
      'DE',
      'US',
      'AU',
      'BR',
      'CA',
      'IN',
      'IT',
      'JP',
      'ES',
      'UK',
    ])
    expect(shown[0]!.attributes('title')).toBe('France (FR)')
  })

  it('starts with the preferred region, then the US, for a book with no region', async () => {
    apiMocks.getApplicationSettings.mockResolvedValue({ defaultSearchRegion: 'de' })
    apiMocks.getAudiobookIdentifiers.mockResolvedValue({
      audiobookId: 7,
      identifiers: [{ ...frenchAsin, region: null }],
    })
    const wrapper = mountModal()
    await flushPromises()

    const shown = row(wrapper).findAll('[data-testid="match-region"] option')
    expect(shown.map((o) => o.text()).slice(0, 3)).toEqual(['DE', 'US', 'AU'])
    expect(field(wrapper, 'match-region').value).toBe('de')
  })

  it('puts the US second when the book is in the preferred region', async () => {
    // The book's region (fr) is the configured one (fr)
    const wrapper = mountModal()
    await flushPromises()

    const shown = row(wrapper).findAll('[data-testid="match-region"] option')
    expect(shown.map((o) => o.text()).slice(0, 3)).toEqual(['FR', 'US', 'AU'])
  })

  it('does not pick the current record found alone: tagged Current, nothing to repair', async () => {
    catalogue({ fr: [frenchVol7] })
    const wrapper = mountModal()
    await flushPromises()

    // The ASIN filled is the book's own
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    const result = row(wrapper).get('[data-testid="find-match-result"]')
    expect(result.classes()).not.toContain('selected')
    expect(result.find('[data-testid="find-match-current"]').exists()).toBe(true)
    expect(row(wrapper).find('[data-testid="match-picked"]').exists()).toBe(false)
    expect(row(wrapper).attributes('data-match-state')).toBe('ignored')
    expect(wrapper.get('[data-testid="repair-confirm"]').text()).toContain('Repair 0')
  })

  it('unpicks the selected row when it is clicked again: no new record, the fields untouched', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)
    const fields = () =>
      ['match-asin', 'match-title', 'match-author', 'match-region'].map(
        (id) => field(wrapper, id).value,
      )
    // Picking left the search as typed
    expect(fields()).toEqual(['', 'Primal Hunter, Vol. 7', 'Zogarth', 'us'])
    expect(row(wrapper).attributes('data-match-state')).toBe('matched')
    const selected = row(wrapper).get('[data-testid="find-match-result"].selected')

    await selected.trigger('click')

    expect(row(wrapper).find('[data-testid="find-match-result"].selected').exists()).toBe(false)
    expect(row(wrapper).get('[data-testid="match-none"]').text()).toBe(
      'No new record yet: search the book and pick it, or type its ASIN',
    )
    expect(row(wrapper).attributes('data-match-state')).toBe('ignored')
    expect(wrapper.get('[data-testid="repair-confirm"]').text()).toContain('Repair 0')
    expect(fields()).toEqual(['', 'Primal Hunter, Vol. 7', 'Zogarth', 'us'])
  })

  it('only rescans when the current record is picked', async () => {
    catalogue({ fr: [frenchVol7] })
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    await row(wrapper).get('[data-testid="find-match-result"]').trigger('click')
    expect(row(wrapper).attributes('data-match-state')).toBe('matched')
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

    // The results say how it ended, and how long it took
    expect(result(wrapper).attributes('data-result-state')).toBe('done')
    expect(result(wrapper).get('[data-testid="repair-result-message"]').text()).toBe(
      'Repaired in 1.23 s: B0HM45F8MW · fr',
    )
  })

  it('shows a repair done in part in orange: the new ASIN saved, its metadata not read', async () => {
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

    expect(result(wrapper).attributes('data-result-state')).toBe('warning')
    expect(result(wrapper).get('[data-testid="repair-result-message"]').text()).toBe(
      'The new ASIN is saved, but the metadata rescan failed (0.5 s): Audible is down',
    )
    // Repaired in part: it can't be organized
    expect(
      result(wrapper).get('[data-testid="repair-result-include"]').attributes('disabled'),
    ).toBeDefined()
  })

  it('shows a failed repair in red, with how long it took, without trailing zeros', async () => {
    let now = 1000
    vi.spyOn(performance, 'now').mockImplementation(() => now)
    apiMocks.updateAudiobookIdentifiers.mockImplementationOnce(async () => {
      now += 250
      throw new Error('Database locked')
    })
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)

    await repair(wrapper)

    expect(result(wrapper).attributes('data-result-state')).toBe('failed')
    expect(result(wrapper).get('[data-testid="repair-result-message"]').text()).toBe(
      'Repair failed (0.25 s): Database locked',
    )
    expect(apiMocks.rescanAudiobookMetadata).not.toHaveBeenCalled()
    expect(wrapper.get('#modal-title h2').text()).toBe('Repair Books - Results - Step 2/3')
    // Nothing repaired: nothing to organize, and the message says so
    expect(wrapper.get('[data-testid="repair-results-message"]').text()).toBe(
      'No book was repaired: there is nothing to organize.',
    )
    const organize = wrapper.get('[data-testid="repair-organize"]')
    expect(organize.text()).toContain('Organize 0')
    expect(organize.attributes('disabled')).toBeDefined()
  })

  it('moves to step 2 on Repair: each book waits its turn in a loading state, Organize disabled', async () => {
    apiMocks.getAudiobook.mockImplementation(async (id: number) =>
      id === 7 ? mismatched : { ...mismatched, id: 8, title: 'Other book' },
    )
    let rescanned!: (value: unknown) => void
    apiMocks.rescanAudiobookMetadata.mockReturnValue(
      new Promise((resolve) => (rescanned = resolve)),
    )
    const wrapper = mountModal([7, 8])
    await flushPromises()
    await searchAndPick(wrapper)

    await wrapper.get('[data-testid="repair-confirm"]').trigger('click')
    await flushPromises()

    // Step 2 at once: the book cards are gone, every book shows on the results step
    expect(wrapper.get('#modal-title h2').text()).toBe('Repair Books - Results - Step 2/3')
    expect(wrapper.find('[data-testid="repair-row-7"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="repair-results-message"]').text()).toContain(
      'Repairing 1 of 1…',
    )
    // The book being repaired: loading, its bar breathing blue, its records before and to come
    expect(result(wrapper).attributes('data-result-state')).toBe('running')
    expect(result(wrapper).get('[data-testid="repair-result-message"]').text()).toBe('Repairing…')
    expect(result(wrapper).find('[data-testid="repair-result-loading"]').exists()).toBe(true)
    expect(result(wrapper).get('[data-testid="repair-result-new"]').text()).toContain('B0BK9W82PQ')
    expect(
      result(wrapper).get('[data-testid="repair-result-include"]').attributes('disabled'),
    ).toBeDefined()
    // The unticked one is skipped, without a loading state
    expect(result(wrapper, 8).attributes('data-result-state')).toBe('skipped')
    expect(result(wrapper, 8).get('[data-testid="repair-result-message"]').text()).toBe(
      'Skipped: not repaired',
    )
    expect(result(wrapper, 8).find('[data-testid="repair-result-loading"]').exists()).toBe(false)
    // Organize waits for the repairs; Cancel is red
    expect(wrapper.find('[data-testid="repair-confirm"]').exists()).toBe(false)
    const organize = wrapper.get('[data-testid="repair-organize"]')
    expect(organize.attributes('disabled')).toBeDefined()
    expect(organize.attributes('title')).toBe('Wait for the repairs to end')
    const cancel = wrapper.get('[data-testid="repair-cancel"]')
    expect(cancel.text()).toBe('Cancel')
    expect(cancel.classes()).toContain('btn-danger')
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe('1 skipped')
    // The progress bar along the bottom: none of the one repair over yet
    const progress = () => wrapper.get('[data-testid="repair-progress"]')
    expect(progress().attributes('aria-valuemax')).toBe('1')
    expect(progress().attributes('aria-valuenow')).toBe('0')

    apiMocks.getAudiobook.mockResolvedValue({ ...mismatched, title: 'Primal Hunter 4' })
    rescanned({ message: 'ok', audiobookId: 7, asin: 'B0BK9W82PQ', region: 'us' })
    await flushPromises()
    expect(progress().attributes('aria-valuenow')).toBe('1')

    // The results: every book, the repaired one ticked for Organize, the skipped one not tickable
    expect(result(wrapper).attributes('data-result-state')).toBe('done')
    expect(result(wrapper, 8).attributes('data-result-state')).toBe('skipped')
    expect(result(wrapper).get('[data-testid="repair-result-include"]').element).toHaveProperty(
      'checked',
      true,
    )
    expect(
      result(wrapper, 8).get('[data-testid="repair-result-include"]').attributes('disabled'),
    ).toBeDefined()
    // Old and new records, the parts that changed in green
    expect(result(wrapper).get('[data-testid="repair-result-old"]').text()).toBe(
      'B0HM45F8MW - Primal Hunter, Vol. 7 - Zogarth - France (FR)',
    )
    const changed = result(wrapper)
      .findAll('[data-testid="repair-result-new"] [data-changed]')
      .map((part) => part.text())
    expect(changed).toEqual(['B0BK9W82PQ', 'Primal Hunter 4', 'United States (US)'])
    // The message not to miss, and Organize for the ticked books, the others counted as skipped
    expect(wrapper.get('[data-testid="repair-results-message"]').text()).toContain(
      '1 book repaired',
    )
    expect(wrapper.get('[data-testid="repair-organize"]').text()).toContain('Organize 1')
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe('1 skipped')

    // Close again, no longer red
    expect(wrapper.get('[data-testid="repair-cancel"]').text()).toBe('Close')
    expect(wrapper.get('[data-testid="repair-cancel"]').classes()).not.toContain('btn-danger')
    expect(result(wrapper).find('[data-testid="repair-result-loading"]').exists()).toBe(false)

    // Unticked: left out of Organize
    await result(wrapper).get('[data-testid="repair-result-include"]').trigger('change')
    expect(wrapper.get('[data-testid="repair-organize"]').text()).toContain('Organize 0')
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe('2 skipped')
  })

  it('repairs one book after the other; Cancel leaves out the ones still waiting', async () => {
    apiMocks.getAudiobook.mockImplementation(async (id: number) => ({ ...mismatched, id }))
    const rescans: ((value: unknown) => void)[] = []
    apiMocks.rescanAudiobookMetadata.mockImplementation(
      () => new Promise((resolve) => rescans.push(resolve)),
    )
    const wrapper = mountModal([7, 8, 9])
    await flushPromises()
    for (const id of [7, 8, 9]) await searchAndPick(wrapper, { id, query: 'Primal Hunter' })

    await repair(wrapper)

    // One at a time: the first runs, the others wait in a loading state
    const states = () => [7, 8, 9].map((id) => result(wrapper, id).attributes('data-result-state'))
    expect(states()).toEqual(['running', 'waiting', 'waiting'])
    expect(rescans).toHaveLength(1)
    expect(result(wrapper, 8).get('[data-testid="repair-result-message"]').text()).toBe(
      'Waiting to be repaired',
    )
    expect(result(wrapper, 8).find('[data-testid="repair-result-loading"]').exists()).toBe(true)

    rescans[0]!({ message: 'ok', audiobookId: 7, asin: 'B0BK9W82PQ', region: 'us' })
    await flushPromises()
    expect(states()).toEqual(['done', 'running', 'waiting'])
    expect(wrapper.get('[data-testid="repair-results-message"]').text()).toContain(
      'Repairing 2 of 3…',
    )

    // Cancel: the waiting book is left out at once, the running one ends first
    await wrapper.get('[data-testid="repair-cancel"]').trigger('click')
    expect(states()).toEqual(['done', 'running', 'skipped'])
    expect(result(wrapper, 9).get('[data-testid="repair-result-message"]').text()).toBe(
      'Canceled: not repaired',
    )
    expect(result(wrapper, 9).get('[data-testid="repair-result-new"]').text()).toBe(
      'None: the book keeps its old record',
    )
    const cancel = wrapper.get('[data-testid="repair-cancel"]')
    expect(cancel.text()).toBe('Canceling…')
    expect(cancel.attributes('disabled')).toBeDefined()

    rescans[1]!({ message: 'ok', audiobookId: 8, asin: 'B0BK9W82PQ', region: 'us' })
    await flushPromises()
    expect(states()).toEqual(['done', 'done', 'skipped'])
    expect(rescans).toHaveLength(2)
    expect(wrapper.get('[data-testid="repair-cancel"]').text()).toBe('Close')
    expect(wrapper.get('[data-testid="repair-organize"]').text()).toContain('Organize 2')
    expect(wrapper.get('[data-testid="repair-organize"]').attributes('disabled')).toBeUndefined()
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
