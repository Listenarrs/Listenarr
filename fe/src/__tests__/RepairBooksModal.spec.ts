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

// Search the row's title and author in a region, then pick the result with that title
// A filled ASIN has priority: emptying it lets Search look for the title and author
async function emptyAsin(wrapper: ReturnType<typeof mountModal>, id = 7) {
  await row(wrapper, id).get('[data-testid="match-asin"]').setValue('')
}

async function searchAndPick(
  wrapper: ReturnType<typeof mountModal>,
  { region = 'us', title = 'Primal Hunter 4' } = {},
) {
  await row(wrapper).get('[data-testid="match-region"]').setValue(region)
  // Changing the region looks the ASIN up there first
  await flushPromises()
  await emptyAsin(wrapper)
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
    expect(wrapper.get('[data-testid="repair-count"]').text()).toBe(
      'Find a new record for each book to repair',
    )
    expect(wrapper.get('[data-testid="match-legend"]').text()).toMatch(
      /Ignored.*Matched.*Invalid.*Error.*Searching/,
    )
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

    // Add New's title search: the region, its language, up to 50 results
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Primal Hunter, Vol. 7',
        author: 'Zogarth',
        region: 'us',
        language: 'english',
        pagination: { page: 1, limit: 50 },
      }),
    )
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
    expect(row(wrapper).get('[data-testid="match-search-empty"]').text()).toContain(
      'No results for "Primal Hunter, Vol. 7" by "Zogarth" in France (FR)',
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
    expect(header()).toBe('Results for "Primal Hunter, Vol. 7" by "Zogarth" in France (FR)')

    await row(wrapper).get('[data-testid="match-title"]').setValue('Something else')
    expect(header()).toBe('Results for "Primal Hunter, Vol. 7" by "Zogarth" in France (FR)')
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

    expect(row(wrapper).get('[data-testid="match-results-header"]').text()).toBe(
      'Results for ASIN "B0BK9W82PQ" in United States (US)',
    )
  })

  it('keeps the results while a new search runs, and after Cancel search says it was canceled', async () => {
    catalogue({ fr: [frenchVol7] })
    const wrapper = mountModal()
    await flushPromises()
    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)

    // A search that never answers: only Cancel search or the timeout ends it
    apiMocks.advancedSearch.mockImplementation(() => new Promise(() => {}))
    await row(wrapper).get('[data-testid="match-title"]').trigger('keydown', { key: 'Enter' })
    await flushPromises()
    // The previous results stay until the new search answers
    expect(row(wrapper).attributes('data-match-state')).toBe('searching')
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)

    await row(wrapper).get('[data-testid="match-cancel-search"]').trigger('click')
    await flushPromises()

    const message = row(wrapper).get('[data-testid="match-search-error"]')
    expect(message.text()).toBe('Search canceled.')
    expect(message.classes()).toContain('match-search-warning')
    expect(row(wrapper).attributes('data-match-state')).toBe('warning')
    expect(row(wrapper).findAll('[data-testid="find-match-result"]')).toHaveLength(1)
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

    await emptyAsin(wrapper)
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="match-search-error"]').text()).toContain(
      'The search failed: API error: 502 Bad Gateway',
    )
    // A failed request is red
    expect(row(wrapper).attributes('data-match-state')).toBe('error')
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

    await emptyAsin(wrapper)
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
    apiMocks.getAudibleMetadata.mockRejectedValueOnce(new Error('API error: 502 Bad Gateway'))
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
    expect(apiMocks.getAudibleMetadata).toHaveBeenLastCalledWith('B0HM45F8MW', 'us')
    expect(row(wrapper).attributes('data-match-state')).toBe('matched')
    expect(row(wrapper).find('[data-testid="match-search"]').exists()).toBe(true)
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

  it('searches one book at a time: the others wait for it', async () => {
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

  it('takes the ASIN and region of the picked result, then repairs with them', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await searchAndPick(wrapper)

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

  it('shows an error when the region has no record for the ASIN, and the book cannot be repaired', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="match-region"]').setValue('us')
    await flushPromises()

    expect(field(wrapper, 'match-asin').value).toBe('B0HM45F8MW')
    expect(row(wrapper).get('[data-testid="match-asin-error"]').text()).toContain(
      'No record for B0HM45F8MW in Audible United States (US)',
    )
    // No record is a search that found nothing: orange
    expect(row(wrapper).attributes('data-match-state')).toBe('warning')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()

    // Another ASIN clears the error, and Search looks it up
    await row(wrapper).get('[data-testid="match-asin"]').setValue('B0BK9W82PQ')
    await row(wrapper).get('[data-testid="match-search"]').trigger('click')
    await flushPromises()
    expect(row(wrapper).find('[data-testid="match-asin-error"]').exists()).toBe(false)
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
    // The record found for the ASIN filled the title and author, searched now
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Primal Hunter 4', author: 'Zogarth' }),
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
    expect(row(wrapper).get('[data-testid="match-picked"]').text()).toContain('Primal Hunter 4')
    // The record found fills the title and author again
    expect(field(wrapper, 'match-title').value).toBe('Primal Hunter 4')
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

    expect(row(wrapper).get('[data-testid="match-asin-error"]').text()).toContain(
      'No record for B0BK9W82PQ in Audible France (FR)',
    )
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('keeps the new record while an ASIN is typed, and Enter looks the typed one up', async () => {
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

    const error = row(wrapper).get('[data-testid="match-search-error"]')
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

  it("offers every region: the book's current one, then the configured one and US, then the rest", async () => {
    apiMocks.getApplicationSettings.mockResolvedValue({ defaultSearchRegion: 'de' })
    const wrapper = mountModal()
    await flushPromises()

    const options = row(wrapper)
      .findAll('[data-testid="match-region"] option')
      .map((o) => o.attributes('value'))

    expect(options.slice(0, 3)).toEqual(['fr', 'de', 'us'])
    expect(options.length).toBeGreaterThan(3)

    // Codes in the field, the full name as each option's tooltip; the others in code order
    const shown = row(wrapper).findAll('[data-testid="match-region"] option')
    expect(shown.map((o) => o.text())).toEqual([
      'FR',
      'DE',
      'US',
      'AU',
      'BR',
      'CA',
      'ES',
      'IN',
      'IT',
      'JP',
      'UK',
    ])
    expect(shown[0]!.attributes('title')).toBe('France (FR)')
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

  it('unpicks the selected row when it is clicked again: no new record', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await searchAndPick(wrapper)
    expect(row(wrapper).attributes('data-match-state')).toBe('matched')
    const selected = row(wrapper).get('[data-testid="find-match-result"].selected')

    await selected.trigger('click')

    expect(row(wrapper).find('[data-testid="find-match-result"].selected').exists()).toBe(false)
    expect(row(wrapper).get('[data-testid="match-none"]').text()).toBe(
      'No new record yet: search the book and pick it, or type its ASIN',
    )
    expect(row(wrapper).attributes('data-match-state')).toBe('ignored')
    expect(wrapper.get('[data-testid="repair-confirm"]').text()).toContain('Repair 0')
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
