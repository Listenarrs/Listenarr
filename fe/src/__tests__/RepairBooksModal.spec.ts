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

// An Audible catalogue per region: ASIN lookups only find what that region sells
function catalogue(byRegion: Record<string, Array<Record<string, unknown>>>) {
  apiMocks.advancedSearch.mockImplementation(
    async ({ asin, region }: { asin?: string; region?: string }) => {
      const books = byRegion[region ?? ''] ?? []
      return asin ? books.filter((b) => b.asin === asin) : books
    },
  )
}

const inputValue = (wrapper: ReturnType<typeof mountModal>, testid: string) =>
  (row(wrapper).get(`[data-testid="${testid}"]`).element as HTMLInputElement).value

function mountModal(ids = [7]) {
  return mount(RepairBooksModal, {
    props: { visible: true, audiobookIds: ids },
    attachTo: document.body,
    global: { plugins: [createPinia()] },
  })
}

const row = (wrapper: ReturnType<typeof mountModal>, id = 7) =>
  wrapper.get(`[data-testid="repair-row-${id}"]`)

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
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('lists each book with its current ASIN and region, the title and author prefilled for searching', async () => {
    const wrapper = mountModal()
    await flushPromises()

    expect(row(wrapper).text()).toContain('B0HM45F8MW')
    expect(row(wrapper).text()).toContain('(fr)')
    expect(
      (row(wrapper).get('[data-testid="record-title"]').element as HTMLInputElement).value,
    ).toBe('Primal Hunter, Vol. 7')
    expect(inputValue(wrapper, 'record-author')).toBe('Zogarth')
    expect(
      (row(wrapper).get('[data-testid="record-region"]').element as HTMLSelectElement).value,
    ).toBe('fr')
  })

  it('searches in the chosen region, ignores empty stubs, and fills the ASIN and region from the pick', async () => {
    apiMocks.advancedSearch.mockResolvedValue([
      {
        asin: 'B0BK9W82PQ',
        title: 'Primal Hunter 4',
        authors: [{ name: 'Zogarth' }],
        language: 'english',
        region: 'us',
      },
      { asin: 'B0ZZZZZZZZ' },
    ])
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-title"]').setValue('The Primal Hunter 4')
    await row(wrapper).get('[data-testid="record-region"]').setValue('us')
    await row(wrapper).get('[data-testid="record-search"]').trigger('click')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith({
      title: 'The Primal Hunter 4',
      author: 'Zogarth',
      region: 'us',
      cap: 8,
    })
    const results = row(wrapper).findAll('[data-testid="record-result"]')
    expect(results).toHaveLength(1)
    await results[0]!.trigger('click')
    expect(
      (row(wrapper).get('[data-testid="record-asin"]').element as HTMLInputElement).value,
    ).toBe('B0BK9W82PQ')
  })

  it('saves the new ASIN as primary, drops the identifiers of the old match, keeps manual ones, then rescans', async () => {
    apiMocks.getAudiobook
      .mockResolvedValueOnce(mismatched)
      .mockResolvedValueOnce({ ...mismatched, title: 'Primal Hunter 4' })
    catalogue({ us: [englishPh4] })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-region"]').setValue('us')
    await row(wrapper).get('[data-testid="record-asin"]').setValue('B0BK9W82PQ')
    await flushPromises()
    await wrapper.get('[data-testid="repair-confirm"]').trigger('click')
    await flushPromises()

    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(7, [
      { type: 'Asin', value: 'B0BK9W82PQ', region: 'us', isPrimary: true, source: 'Manual' },
      { type: 'Isbn', value: '9781039400740', region: null, isPrimary: false, source: 'Manual' },
    ])
    expect(apiMocks.rescanAudiobookMetadata).toHaveBeenCalledWith(7)
    expect(wrapper.text()).toContain('Primal Hunter 4')
    expect(wrapper.text()).toContain('B0BK9W82PQ · us')
  })

  it('looks the ASIN up again when the region changes, and blocks the repair when that region has no record', async () => {
    catalogue({ us: [englishPh4] })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-region"]').setValue('us')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith({
      asin: 'B0HM45F8MW',
      region: 'us',
      cap: 5,
    })
    expect(row(wrapper).get('[data-testid="record-asin-not-found"]').text()).toContain('B0HM45F8MW')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()

    await row(wrapper).get('[data-testid="record-search"]').trigger('click')
    await flushPromises()
    await row(wrapper).get('[data-testid="record-result"]').trigger('click')

    expect(row(wrapper).find('[data-testid="record-asin-not-found"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeUndefined()
  })

  it('keeps the ASIN and fills the title and author when the new region has it', async () => {
    catalogue({
      uk: [{ ...englishPh4, asin: 'B0HM45F8MW', title: 'Primal Hunter 7', region: 'uk' }],
    })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-region"]').setValue('uk')
    await flushPromises()

    expect(inputValue(wrapper, 'record-asin')).toBe('B0HM45F8MW')
    expect(inputValue(wrapper, 'record-title')).toBe('Primal Hunter 7')
    expect(inputValue(wrapper, 'record-author')).toBe('Zogarth')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeUndefined()

    await wrapper.get('[data-testid="repair-confirm"]').trigger('click')
    await flushPromises()
    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(
      7,
      expect.arrayContaining([
        expect.objectContaining({ value: 'B0HM45F8MW', region: 'uk', isPrimary: true }),
      ]),
    )
  })

  it('clears the old ASIN when the author changes', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-author"]').setValue('Someone Else')

    expect(inputValue(wrapper, 'record-asin')).toBe('')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('clears the old ASIN when the name changes', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-title"]').setValue('The Primal Hunter 4')

    expect(
      (row(wrapper).get('[data-testid="record-asin"]').element as HTMLInputElement).value,
    ).toBe('')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('keeps a picked ASIN when the name is edited afterwards', async () => {
    apiMocks.advancedSearch.mockResolvedValue([
      { asin: 'B0BK9W82PQ', title: 'Primal Hunter 4', language: 'english', region: 'us' },
    ])
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-region"]').setValue('us')
    await row(wrapper).get('[data-testid="record-search"]').trigger('click')
    await flushPromises()
    await row(wrapper).get('[data-testid="record-result"]').trigger('click')
    await row(wrapper).get('[data-testid="record-title"]').setValue('Primal Hunter 4 (Zogarth)')

    expect(
      (row(wrapper).get('[data-testid="record-asin"]').element as HTMLInputElement).value,
    ).toBe('B0BK9W82PQ')
  })

  it('reverts a book to the values it had before the manual changes', async () => {
    const wrapper = mountModal()
    await flushPromises()
    const revert = row(wrapper).get('[data-testid="repair-revert"]')
    expect(revert.attributes('disabled')).toBeDefined()

    await row(wrapper).get('[data-testid="record-title"]').setValue('The Primal Hunter 4')
    await row(wrapper).get('[data-testid="record-region"]').setValue('us')
    expect(row(wrapper).get('[data-testid="repair-revert"]').attributes('disabled')).toBeUndefined()

    await row(wrapper).get('[data-testid="repair-revert"]').trigger('click')

    expect(
      (row(wrapper).get('[data-testid="record-title"]').element as HTMLInputElement).value,
    ).toBe('Primal Hunter, Vol. 7')
    expect(
      (row(wrapper).get('[data-testid="record-region"]').element as HTMLSelectElement).value,
    ).toBe('fr')
    expect(
      (row(wrapper).get('[data-testid="record-asin"]').element as HTMLInputElement).value,
    ).toBe('B0HM45F8MW')
    expect(row(wrapper).find('[data-testid="record-asin-cleared"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeUndefined()
  })

  it('looks a typed ASIN up in the selected region and fills the title and author', async () => {
    catalogue({ us: [englishPh4] })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-region"]').setValue('us')
    await flushPromises()
    await row(wrapper).get('[data-testid="record-asin"]').setValue('B0BK9W82PQ')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenLastCalledWith({
      asin: 'B0BK9W82PQ',
      region: 'us',
      cap: 5,
    })
    expect(inputValue(wrapper, 'record-title')).toBe('Primal Hunter 4')
    expect(inputValue(wrapper, 'record-author')).toBe('Zogarth')
    expect(row(wrapper).find('[data-testid="record-asin-not-found"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeUndefined()
  })

  it('blocks the repair when a typed ASIN has no record in the selected region', async () => {
    catalogue({ us: [englishPh4] })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-asin"]').setValue('B0BK9W82PQ')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenLastCalledWith({
      asin: 'B0BK9W82PQ',
      region: 'fr',
      cap: 5,
    })
    expect(row(wrapper).get('[data-testid="record-asin-not-found"]').text()).toContain('France')
    expect(inputValue(wrapper, 'record-title')).toBe('Primal Hunter, Vol. 7')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('searches the title and author, never the ASIN of the old match', async () => {
    apiMocks.advancedSearch.mockResolvedValue([])
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-search"]').trigger('click')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenCalledTimes(1)
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith({
      title: 'Primal Hunter, Vol. 7',
      author: 'Zogarth',
      region: 'fr',
      cap: 8,
    })
  })

  it("offers every region: the book's current one, then the configured one and US, then the rest alphabetically", async () => {
    apiMocks.getAudiobookIdentifiers.mockResolvedValue({
      audiobookId: 7,
      identifiers: [{ ...frenchAsin, region: 'uk' }],
    })
    const wrapper = mountModal()
    await flushPromises()

    const options = row(wrapper)
      .findAll('[data-testid="record-region"] option')
      .map((o) => (o.element as HTMLOptionElement).value)
    expect(options.slice(0, 3)).toEqual(['uk', 'fr', 'us'])
    const labels = row(wrapper)
      .findAll('[data-testid="record-region"] option')
      .slice(3)
      .map((o) => o.text())
    expect(labels).toEqual([...labels].sort((x, y) => x.localeCompare(y)))
    expect(options.length).toBeGreaterThan(3)
    expect(
      (row(wrapper).get('[data-testid="record-region"]').element as HTMLSelectElement).value,
    ).toBe('uk')
  })

  it('searches in the region picked from the full list', async () => {
    apiMocks.advancedSearch.mockResolvedValue([])
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-region"]').setValue('de')
    await row(wrapper).get('[data-testid="record-title"]').setValue('Primal Hunter 4')
    await row(wrapper).get('[data-testid="record-search"]').trigger('click')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenLastCalledWith({
      title: 'Primal Hunter 4',
      author: 'Zogarth',
      region: 'de',
      cap: 8,
    })
  })

  it('only rescans when the ASIN and region are unchanged', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await wrapper.get('[data-testid="repair-confirm"]').trigger('click')
    await flushPromises()

    expect(apiMocks.updateAudiobookIdentifiers).not.toHaveBeenCalled()
    expect(apiMocks.rescanAudiobookMetadata).toHaveBeenCalledWith(7)
  })

  it('retries the rescan once when it is refused for coming too soon', async () => {
    vi.useFakeTimers()
    apiMocks.rescanAudiobookMetadata
      .mockRejectedValueOnce(Object.assign(new Error('Too many requests'), { status: 429 }))
      .mockResolvedValueOnce({ message: 'ok', audiobookId: 7, asin: 'B0HM45F8MW', region: 'fr' })
    const wrapper = mountModal()
    await flushPromises()

    await wrapper.get('[data-testid="repair-confirm"]').trigger('click')
    await flushPromises()
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()

    expect(apiMocks.rescanAudiobookMetadata).toHaveBeenCalledTimes(2)
    expect(wrapper.find('.result-row.success').exists()).toBe(true)
  })

  it('flags a malformed ASIN and does not repair the book', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="record-asin"]').setValue('B0BK9')
    await flushPromises()

    expect(row(wrapper).get('[data-testid="record-asin-invalid"]').text()).toContain(
      'B and 9 letters or digits',
    )
    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="repair-confirm"]').trigger('click')
    await flushPromises()
    expect(apiMocks.updateAudiobookIdentifiers).not.toHaveBeenCalled()
    expect(apiMocks.rescanAudiobookMetadata).not.toHaveBeenCalled()
  })

  it('offers to organize the repaired books', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await wrapper.get('[data-testid="repair-confirm"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="repair-organize"]').trigger('click')

    expect(wrapper.emitted('organize')).toEqual([[[7]]])
  })
})
