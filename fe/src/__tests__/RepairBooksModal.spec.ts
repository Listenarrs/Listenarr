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
  getLibraryIssue: vi.fn(),
  getLibraryIssues: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  apiService: apiMocks,
}))

import RepairBooksModal from '@/components/domain/repair/RepairBooksModal.vue'
import LibraryImportSearchModal from '@/components/domain/audiobook/LibraryImportSearchModal.vue'

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

async function openFindMatch(wrapper: ReturnType<typeof mountModal>) {
  await row(wrapper).get('[data-testid="repair-find-match"]').trigger('click')
  await flushPromises()
  return wrapper.getComponent(LibraryImportSearchModal)
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
    apiMocks.getLibraryIssue.mockResolvedValue({ id: 7, issues: [] })
    apiMocks.getLibraryIssues.mockResolvedValue([])
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('lists each book with its current ASIN and region', async () => {
    const wrapper = mountModal()
    await flushPromises()

    expect(row(wrapper).text()).toContain('Primal Hunter, Vol. 7')
    expect(row(wrapper).text()).toMatch(/B0HM45F8MW\s+\(fr\)/)
    expect(field(wrapper, 'repair-asin').value).toBe('B0HM45F8MW')
    expect(field(wrapper, 'repair-region').value).toBe('fr')
  })

  it('names each field above it, in the order ASIN, Title, Author, Region', async () => {
    const wrapper = mountModal()
    await flushPromises()

    expect(
      row(wrapper)
        .findAll('.repair-field-label')
        .map((l) => l.text()),
    ).toEqual(['ASIN', 'Title', 'Author', 'Region'])
  })

  it('empties the ASIN when the title, author or region changes, and asks to pick the book again', async () => {
    for (const [testid, value] of [
      ['repair-title', 'The Primal Hunter 4'],
      ['repair-author', 'Someone else'],
      ['repair-region', 'us'],
    ]) {
      const wrapper = mountModal()
      await flushPromises()

      await row(wrapper).get(`[data-testid="${testid}"]`).setValue(value)

      expect(field(wrapper, 'repair-asin').value).toBe('')
      expect(row(wrapper).get('[data-testid="repair-invalid-asin"]').text()).toContain(
        'No ASIN: use Find Match to pick the book',
      )
      expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
      wrapper.unmount()
    }
  })

  it('keeps the ASIN and region of a record picked in Find Match', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="repair-title"]').setValue('Primal Hunter 4')

    const findMatch = await openFindMatch(wrapper)
    findMatch.vm.$emit('select', englishPh4)
    await flushPromises()

    expect(field(wrapper, 'repair-asin').value).toBe('B0BK9W82PQ')
    expect(field(wrapper, 'repair-region').value).toBe('us')
  })

  it("opens canary's Find Match on the book's title, author and region", async () => {
    const wrapper = mountModal()
    await flushPromises()

    const findMatch = await openFindMatch(wrapper)

    expect(findMatch.props('target')).toEqual({
      name: 'Primal Hunter, Vol. 7',
      query: 'Primal Hunter, Vol. 7',
      author: 'Zogarth',
      region: 'fr',
    })
    expect(findMatch.text()).toContain('Find Match — Primal Hunter, Vol. 7')
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Primal Hunter, Vol. 7', author: 'Zogarth', region: 'fr' }),
      expect.any(AbortSignal),
    )
  })

  it('searches the title and author typed on the row, from Find Match or with Enter, and Revert restores them', async () => {
    const wrapper = mountModal()
    await flushPromises()
    expect(field(wrapper, 'repair-title').value).toBe('Primal Hunter, Vol. 7')
    expect(field(wrapper, 'repair-author').value).toBe('Zogarth')

    await row(wrapper).get('[data-testid="repair-title"]').setValue('The Primal Hunter 4')
    await row(wrapper).get('[data-testid="repair-title"]').trigger('keydown', { key: 'Enter' })
    await flushPromises()

    const findMatch = wrapper.getComponent(LibraryImportSearchModal)
    expect(findMatch.props('target')).toMatchObject({
      name: 'Primal Hunter, Vol. 7',
      query: 'The Primal Hunter 4',
      author: 'Zogarth',
    })
    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'The Primal Hunter 4', author: 'Zogarth' }),
      expect.any(AbortSignal),
    )

    findMatch.vm.$emit('close')
    await flushPromises()
    await row(wrapper).get('[data-testid="repair-revert"]').trigger('click')
    expect(field(wrapper, 'repair-title').value).toBe('Primal Hunter, Vol. 7')
  })

  it('takes the ASIN and region of the record picked in Find Match, then repairs with them', async () => {
    const wrapper = mountModal()
    await flushPromises()
    const findMatch = await openFindMatch(wrapper)
    ;(findMatch.get('select').element as HTMLSelectElement).value = 'us'
    await findMatch.get('select').trigger('change')
    await flushPromises()

    await findMatch.get('.result-item').trigger('click')
    await flushPromises()

    expect(wrapper.findComponent(LibraryImportSearchModal).exists()).toBe(false)
    expect(field(wrapper, 'repair-asin').value).toBe('B0BK9W82PQ')
    expect(field(wrapper, 'repair-region').value).toBe('us')
    expect(row(wrapper).get('[data-testid="repair-picked"]').text()).toContain('Primal Hunter 4')

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
    const findMatch = await openFindMatch(wrapper)
    findMatch.vm.$emit('select', englishPh4)
    await flushPromises()

    await repair(wrapper)

    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(7, [
      { type: 'Asin', value: 'B0BK9W82PQ', region: 'us', isPrimary: true, source: 'Manual' },
      { type: 'Isbn', value: '9781039400740', region: null, isPrimary: false, source: 'Manual' },
    ])
  })

  it('checks a typed ASIN in its region with the Find Match search before saving it', async () => {
    const wrapper = mountModal()
    await flushPromises()
    // The region first: changing it empties the ASIN
    await row(wrapper).get('[data-testid="repair-region"]').setValue('us')
    await row(wrapper).get('[data-testid="repair-asin"]').setValue('b0bk9w82pq')

    await repair(wrapper)

    expect(apiMocks.advancedSearch).toHaveBeenCalledWith(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us', cap: 5 }),
    )
    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalledWith(
      7,
      expect.arrayContaining([expect.objectContaining({ value: 'B0BK9W82PQ', region: 'us' })]),
    )
  })

  it('does not save a typed ASIN its region has no record for', async () => {
    const wrapper = mountModal()
    await flushPromises()
    await row(wrapper).get('[data-testid="repair-asin"]').setValue('B0BK9W82PQ')

    await repair(wrapper)

    expect(apiMocks.updateAudiobookIdentifiers).not.toHaveBeenCalled()
    expect(apiMocks.rescanAudiobookMetadata).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Audible France (FR) has no record for B0BK9W82PQ')
  })

  it('forgets the picked record once the ASIN is typed again, and checks the typed one', async () => {
    const wrapper = mountModal()
    await flushPromises()
    const findMatch = await openFindMatch(wrapper)
    findMatch.vm.$emit('select', englishPh4)
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-asin"]').setValue('B0BK9W82PQ')

    expect(row(wrapper).find('[data-testid="repair-picked"]').exists()).toBe(false)
    await repair(wrapper)
    expect(apiMocks.advancedSearch).toHaveBeenLastCalledWith(
      expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' }),
    )
    expect(apiMocks.updateAudiobookIdentifiers).toHaveBeenCalled()
  })

  it('flags a malformed ASIN and does not let the repair start', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-asin"]').setValue('B0BK9')

    expect(row(wrapper).find('[data-testid="repair-invalid-asin"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="repair-confirm"]').attributes('disabled')).toBeDefined()
  })

  it('reverts a book to its ASIN and region before the changes', async () => {
    const wrapper = mountModal()
    await flushPromises()
    const findMatch = await openFindMatch(wrapper)
    findMatch.vm.$emit('select', englishPh4)
    await flushPromises()

    await row(wrapper).get('[data-testid="repair-revert"]').trigger('click')

    expect(field(wrapper, 'repair-asin').value).toBe('B0HM45F8MW')
    expect(field(wrapper, 'repair-region').value).toBe('fr')
    expect(row(wrapper).find('[data-testid="repair-picked"]').exists()).toBe(false)
    expect(row(wrapper).get('[data-testid="repair-revert"]').attributes('disabled')).toBeDefined()
  })

  it("offers every region: the book's current one, then the configured one and US, then the rest", async () => {
    apiMocks.getApplicationSettings.mockResolvedValue({ defaultSearchRegion: 'de' })
    const wrapper = mountModal()
    await flushPromises()

    const options = row(wrapper)
      .findAll('[data-testid="repair-region"] option')
      .map((o) => o.attributes('value'))

    expect(options.slice(0, 3)).toEqual(['fr', 'de', 'us'])
    expect(options.length).toBeGreaterThan(3)
  })

  it('only rescans when the ASIN and region are unchanged', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await repair(wrapper)

    expect(apiMocks.advancedSearch).not.toHaveBeenCalled()
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

    const done = repair(wrapper)
    await vi.advanceTimersByTimeAsync(3000)
    await done
    await flushPromises()

    expect(apiMocks.rescanAudiobookMetadata).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('B0HM45F8MW · fr')
  })

  it('offers to organize the repaired books', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await repair(wrapper)
    await wrapper.get('[data-testid="repair-organize"]').trigger('click')

    expect(wrapper.emitted('organize')).toEqual([[[7]]])
  })

  it('shows the current problems the server reports for the book, in orange as on the Issues page', async () => {
    apiMocks.getLibraryIssue.mockResolvedValue({ id: 7, issues: ['missing_asin'] })
    const wrapper = mountModal()
    await flushPromises()

    expect(apiMocks.getLibraryIssue).toHaveBeenCalledWith(7)
    const pills = row(wrapper).findAll('[data-testid="repair-issue"]')
    expect(pills.map((p) => p.text())).toEqual(['No ASIN'])
    expect(pills[0]!.classes()).toContain('pill-warning')
  })

  it('shows no problem when the issues cannot be read', async () => {
    apiMocks.getLibraryIssue.mockRejectedValue(new Error('Not found'))
    const wrapper = mountModal()
    await flushPromises()

    expect(row(wrapper).findAll('[data-testid="repair-issue"]')).toHaveLength(0)
    expect(row(wrapper).text()).toContain('Primal Hunter, Vol. 7')
  })

  it('refreshes the Issues list once the books are repaired', async () => {
    const wrapper = mountModal()
    await flushPromises()

    await repair(wrapper)

    expect(apiMocks.getLibraryIssues).toHaveBeenCalled()
  })
})
