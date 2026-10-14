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

  it('lists each book with its current ASIN and region, the name prefilled for searching', async () => {
    const wrapper = mountModal()
    await flushPromises()

    expect(row(wrapper).text()).toContain('B0HM45F8MW')
    expect(row(wrapper).text()).toContain('(fr)')
    expect(
      (row(wrapper).get('[data-testid="repair-query"]').element as HTMLInputElement).value,
    ).toBe('Primal Hunter, Vol. 7')
    expect(
      (row(wrapper).get('[data-testid="repair-region"]').element as HTMLSelectElement).value,
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

    await row(wrapper).get('[data-testid="repair-query"]').setValue('The Primal Hunter 4')
    await row(wrapper).get('[data-testid="repair-region"]').setValue('us')
    await row(wrapper).get('[data-testid="repair-search"]').trigger('click')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith({
      title: 'The Primal Hunter 4',
      region: 'us',
      cap: 8,
    })
    const results = row(wrapper).findAll('[data-testid="repair-result"]')
    expect(results).toHaveLength(1)
    await results[0]!.trigger('click')
    expect(
      (row(wrapper).get('[data-testid="repair-asin"]').element as HTMLInputElement).value,
    ).toBe('B0BK9W82PQ')
  })

  it('saves the new ASIN as primary, drops the identifiers of the old match, keeps manual ones, then rescans', async () => {
    apiMocks.getAudiobook
      .mockResolvedValueOnce(mismatched)
      .mockResolvedValueOnce({ ...mismatched, title: 'Primal Hunter 4' })
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-region"]').setValue('us')
    await row(wrapper).get('[data-testid="repair-asin"]').setValue('B0BK9W82PQ')
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

  it('clears the old ASIN when the region changes and waits for a pick before repairing', async () => {
    apiMocks.advancedSearch.mockResolvedValue([
      { asin: 'B0BK9W82PQ', title: 'Primal Hunter 4', language: 'english', region: 'us' },
    ])
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-region"]').setValue('us')
    expect(
      (row(wrapper).get('[data-testid="repair-asin"]').element as HTMLInputElement).value,
    ).toBe('')
    expect(row(wrapper).find('[data-testid="repair-asin-cleared"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()

    await row(wrapper).get('[data-testid="repair-search"]').trigger('click')
    await flushPromises()
    await row(wrapper).get('[data-testid="repair-result"]').trigger('click')

    expect(row(wrapper).find('[data-testid="repair-asin-cleared"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeUndefined()
  })

  it('clears the old ASIN when the name changes', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-query"]').setValue('The Primal Hunter 4')

    expect(
      (row(wrapper).get('[data-testid="repair-asin"]').element as HTMLInputElement).value,
    ).toBe('')
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('keeps a picked ASIN when the name is edited afterwards', async () => {
    apiMocks.advancedSearch.mockResolvedValue([
      { asin: 'B0BK9W82PQ', title: 'Primal Hunter 4', language: 'english', region: 'us' },
    ])
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-region"]').setValue('us')
    await row(wrapper).get('[data-testid="repair-search"]').trigger('click')
    await flushPromises()
    await row(wrapper).get('[data-testid="repair-result"]').trigger('click')
    await row(wrapper).get('[data-testid="repair-query"]').setValue('Primal Hunter 4 (Zogarth)')

    expect(
      (row(wrapper).get('[data-testid="repair-asin"]').element as HTMLInputElement).value,
    ).toBe('B0BK9W82PQ')
  })

  it('reverts a book to the values it had before the manual changes', async () => {
    const wrapper = mountModal()
    await flushPromises()
    const revert = row(wrapper).get('[data-testid="repair-revert"]')
    expect(revert.attributes('disabled')).toBeDefined()

    await row(wrapper).get('[data-testid="repair-query"]').setValue('The Primal Hunter 4')
    await row(wrapper).get('[data-testid="repair-region"]').setValue('us')
    expect(row(wrapper).get('[data-testid="repair-revert"]').attributes('disabled')).toBeUndefined()

    await row(wrapper).get('[data-testid="repair-revert"]').trigger('click')

    expect(
      (row(wrapper).get('[data-testid="repair-query"]').element as HTMLInputElement).value,
    ).toBe('Primal Hunter, Vol. 7')
    expect(
      (row(wrapper).get('[data-testid="repair-region"]').element as HTMLSelectElement).value,
    ).toBe('fr')
    expect(
      (row(wrapper).get('[data-testid="repair-asin"]').element as HTMLInputElement).value,
    ).toBe('B0HM45F8MW')
    expect(row(wrapper).find('[data-testid="repair-asin-cleared"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeUndefined()
  })

  it('searches the ASIN typed for the book first, to show which book it is', async () => {
    apiMocks.advancedSearch.mockResolvedValue([
      { asin: 'B0BK9W82PQ', title: 'Primal Hunter 4', language: 'english', region: 'us' },
    ])
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-region"]').setValue('us')
    await row(wrapper).get('[data-testid="repair-asin"]').setValue('B0BK9W82PQ')
    await row(wrapper).get('[data-testid="repair-search"]').trigger('click')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith({
      asin: 'B0BK9W82PQ',
      region: 'us',
      cap: 5,
    })
    expect(row(wrapper).text()).toContain('Primal Hunter 4')
  })

  it('never searches the ASIN of the old match: it searches the name instead', async () => {
    apiMocks.advancedSearch.mockResolvedValue([])
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-search"]').trigger('click')
    await flushPromises()

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith({
      title: 'Primal Hunter, Vol. 7',
      region: 'fr',
      cap: 8,
    })
  })

  it('only offers the configured region and the US fallback', async () => {
    const wrapper = mountModal()
    await flushPromises()

    const options = row(wrapper)
      .findAll('[data-testid="repair-region"] option')
      .map((o) => (o.element as HTMLOptionElement).value)
    expect(options.sort()).toEqual(['fr', 'us'])
  })

  it("keeps a book's existing other region available so it can be shown and reverted", async () => {
    apiMocks.getAudiobookIdentifiers.mockResolvedValue({
      audiobookId: 7,
      identifiers: [{ ...frenchAsin, region: 'uk' }],
    })
    const wrapper = mountModal()
    await flushPromises()

    const options = row(wrapper)
      .findAll('[data-testid="repair-region"] option')
      .map((o) => (o.element as HTMLOptionElement).value)
    expect(options.sort()).toEqual(['fr', 'uk', 'us'])
    expect(
      (row(wrapper).get('[data-testid="repair-region"]').element as HTMLSelectElement).value,
    ).toBe('uk')
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

  it('reports an invalid ASIN without touching the book', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-asin"]').setValue('B0BK9')
    await wrapper.get('[data-testid="repair-confirm"]').trigger('click')
    await flushPromises()

    expect(apiMocks.updateAudiobookIdentifiers).not.toHaveBeenCalled()
    expect(apiMocks.rescanAudiobookMetadata).not.toHaveBeenCalled()
    expect(wrapper.get('.result-row.error').text()).toContain('10 characters')
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
