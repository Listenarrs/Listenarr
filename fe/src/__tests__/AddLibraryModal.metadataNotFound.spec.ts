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
import { vi, describe, it, expect, beforeEach } from 'vitest'

const apiMocks = vi.hoisted(() => ({
  getAudibleMetadata: vi.fn(),
  previewLibraryPath: vi.fn(),
  getApplicationSettings: vi.fn(),
  getQualityProfiles: vi.fn(),
  getRootFolders: vi.fn(),
  addToLibrary: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  apiService: apiMocks,
}))

import AddLibraryModal from '@/components/domain/audiobook/AddLibraryModal.vue'

// A book only sold on audible.com, seen from an instance configured for France
const bookFromFiles = {
  title: 'A Princess of Mars',
  authors: [],
  narrators: [],
  asin: 'B008DFUGCQ',
  source: 'Audible',
}

const usRecord = {
  metadata: {
    asin: 'B008DFUGCQ',
    title: 'A Princess of Mars',
    authors: [{ name: 'Edgar Rice Burroughs' }],
    narrators: [{ name: 'Bob Thomley' }],
    region: 'us',
  },
  source: 'Audible',
}

// What Audible returns for an ASIN it doesn't sell in that market
const emptyFrenchStub = {
  metadata: { asin: 'B008DFUGCQ', authors: [], narrators: [], region: 'fr' },
  source: 'Audible',
}

const notFound = Object.assign(new Error('No metadata found for ASIN: B008DFUGCQ'), { status: 404 })

function mountModal() {
  return mount(AddLibraryModal, {
    props: { visible: true, book: bookFromFiles },
    attachTo: document.body,
    global: { plugins: [createPinia()] },
  })
}

describe('AddLibraryModal when the configured region has no record for the ASIN', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    apiMocks.getApplicationSettings.mockResolvedValue({
      outputPath: '/library',
      defaultSearchRegion: 'fr',
    })
    apiMocks.getQualityProfiles.mockResolvedValue([])
    apiMocks.getRootFolders.mockResolvedValue([])
    apiMocks.previewLibraryPath.mockResolvedValue({
      fullPath: '/library/Edgar Rice Burroughs/A Princess of Mars',
      relativePath: 'Edgar Rice Burroughs/A Princess of Mars',
    })
    apiMocks.addToLibrary.mockResolvedValue({ message: 'Added', audiobook: { id: 1 } })
  })

  it.each([
    ['an empty product stub', () => apiMocks.getAudibleMetadata.mockResolvedValue(emptyFrenchStub)],
    ['a 404', () => apiMocks.getAudibleMetadata.mockRejectedValue(notFound)],
  ])(
    'warns and blocks the add instead of saving a book without metadata (%s)',
    async (_, arrange) => {
      arrange()
      const wrapper = mountModal()
      await flushPromises()

      expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('B008DFUGCQ', 'fr')
      expect(wrapper.find('[data-testid="metadata-not-found"]').text()).toContain('France (FR)')
      const add = wrapper.get('button.btn-primary')
      expect(add.attributes('disabled')).toBeDefined()

      await add.trigger('click')
      await flushPromises()
      expect(apiMocks.addToLibrary).not.toHaveBeenCalled()
    },
  )

  it('looks the book up again in the region the user picks and keeps that region', async () => {
    apiMocks.getAudibleMetadata.mockRejectedValueOnce(notFound).mockResolvedValueOnce(usRecord)
    const wrapper = mountModal()
    await flushPromises()

    await wrapper.get('[data-testid="metadata-retry-region"]').setValue('us')
    await wrapper.get('[data-testid="metadata-retry"]').trigger('click')
    await flushPromises()

    expect(apiMocks.getAudibleMetadata).toHaveBeenLastCalledWith('B008DFUGCQ', 'us')
    expect(wrapper.find('[data-testid="metadata-not-found"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('Edgar Rice Burroughs')

    await wrapper.get('button.btn-primary').trigger('click')
    await flushPromises()
    expect(apiMocks.addToLibrary).toHaveBeenCalledWith(
      expect.objectContaining({ authors: ['Edgar Rice Burroughs'], region: 'us' }),
      expect.any(Object),
    )
  })

  it('keeps a title edited while the regional retry is pending, with the region that found it', async () => {
    let answerUs: (record: unknown) => void = () => {}
    apiMocks.getAudibleMetadata
      .mockRejectedValueOnce(notFound)
      .mockImplementationOnce(() => new Promise((resolve) => (answerUs = resolve)))
    const wrapper = mountModal()
    await flushPromises()

    await wrapper.get('[data-testid="metadata-retry-region"]').setValue('us')
    await wrapper.get('[data-testid="metadata-retry"]').trigger('click')
    // Editing while the US lookup runs
    await wrapper.get('button.metadata-toggle-btn').trigger('click')
    await flushPromises()
    const title = wrapper
      .findAll('.metadata-edit-grid .detail-item')
      .find((item) => item.text().includes('Title'))!
      .find('input')
    await title.setValue('A Princess of Mars (my edition)')
    answerUs(usRecord)
    await flushPromises()

    expect((title.element as HTMLInputElement).value).toBe('A Princess of Mars (my edition)')
    await wrapper.get('button.btn-primary').trigger('click')
    await flushPromises()
    expect(apiMocks.addToLibrary).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'A Princess of Mars (my edition)', region: 'us' }),
      expect.any(Object),
    )
  })

  it('keeps the region of the lookup when the response has no top-level region', async () => {
    // e.g. a book from "Scan for unmatched files": no region of its own, found in the configured one
    apiMocks.getAudibleMetadata.mockResolvedValue({
      metadata: { ...usRecord.metadata, title: 'L’étroit chemin', region: undefined },
      source: 'Audible',
    })
    const wrapper = mountModal()
    await flushPromises()

    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('B008DFUGCQ', 'fr')
    await wrapper.get('button.btn-primary').trigger('click')
    await flushPromises()
    expect(apiMocks.addToLibrary).toHaveBeenCalledWith(
      expect.objectContaining({ region: 'fr' }),
      expect.any(Object),
    )
  })

  it('keeps the region that found the book, not the one that failed, when the response has none', async () => {
    apiMocks.getAudibleMetadata
      .mockRejectedValueOnce(notFound)
      .mockResolvedValueOnce({ metadata: { ...usRecord.metadata, region: undefined } })
    const wrapper = mount(AddLibraryModal, {
      props: { visible: true, book: { ...bookFromFiles, region: 'fr' } },
      attachTo: document.body,
      global: { plugins: [createPinia()] },
    })
    await flushPromises()

    await wrapper.get('[data-testid="metadata-retry-region"]').setValue('us')
    await wrapper.get('[data-testid="metadata-retry"]').trigger('click')
    await flushPromises()

    await wrapper.get('button.btn-primary').trigger('click')
    await flushPromises()
    expect(apiMocks.addToLibrary).toHaveBeenCalledWith(
      expect.objectContaining({ region: 'us' }),
      expect.any(Object),
    )
  })

  it('keeps warning when the other region has no record either', async () => {
    apiMocks.getAudibleMetadata.mockRejectedValue(notFound)
    const wrapper = mountModal()
    await flushPromises()

    await wrapper.get('[data-testid="metadata-retry-region"]').setValue('uk')
    await wrapper.get('[data-testid="metadata-retry"]').trigger('click')
    await flushPromises()

    expect(apiMocks.getAudibleMetadata).toHaveBeenLastCalledWith('B008DFUGCQ', 'uk')
    expect(wrapper.find('[data-testid="metadata-not-found"]').text()).toContain(
      'United Kingdom (UK)',
    )
    expect(wrapper.get('button.btn-primary').attributes('disabled')).toBeDefined()
  })

  it('never switches to another catalogue on its own', async () => {
    apiMocks.getAudibleMetadata.mockRejectedValue(notFound)
    const wrapper = mountModal()
    await flushPromises()

    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledTimes(1)
    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('B008DFUGCQ', 'fr')
    expect(wrapper.get('[data-testid="metadata-not-found"]').text()).toContain('France (FR)')
  })

  it('offers the tried region, then US, then the other regions alphabetically', async () => {
    apiMocks.getAudibleMetadata.mockRejectedValue(notFound)
    const wrapper = mountModal()
    await flushPromises()

    const options = wrapper
      .findAll('[data-testid="metadata-retry-region"] option')
      .map((o) => (o.element as HTMLOptionElement).value)
    expect(options.slice(0, 2)).toEqual(['fr', 'us'])
    const labels = wrapper
      .findAll('[data-testid="metadata-retry-region"] option')
      .slice(2)
      .map((o) => o.text())
    expect(labels).toEqual([...labels].sort((x, y) => x.localeCompare(y)))
  })

  it('shows no warning when the configured region has the book', async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue({
      ...usRecord,
      metadata: { ...usRecord.metadata, region: 'fr' },
    })
    const wrapper = mountModal()
    await flushPromises()

    expect(wrapper.find('[data-testid="metadata-not-found"]').exists()).toBe(false)
    expect(wrapper.get('button.btn-primary').attributes('disabled')).toBeUndefined()
  })
})
