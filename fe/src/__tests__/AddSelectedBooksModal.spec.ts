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
import { describe, it, expect, beforeEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import AddSelectedBooksModal from '@/components/domain/audiobook/AddSelectedBooksModal.vue'

const { mockAddToLibrary } = vi.hoisted(() => ({ mockAddToLibrary: vi.fn() }))

vi.mock('@/services/api', () => ({ apiService: { addToLibrary: mockAddToLibrary } }))
vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn() }),
}))
vi.mock('@/utils/logger', () => ({ logger: { error: vi.fn(), warn: vi.fn(), debug: vi.fn() } }))
vi.mock('@/stores/configuration', () => ({
  useConfigurationStore: () => ({
    qualityProfiles: [{ id: 1, name: 'Default', isDefault: true }],
    loadQualityProfiles: vi.fn(),
  }),
}))
vi.mock('@/stores/rootFolders', () => ({
  useRootFoldersStore: () => ({
    folders: [{ id: 1, path: '/audiobooks', isDefault: true }],
    load: vi.fn(),
  }),
}))

const books = [
  { asin: 'A1', title: 'Book A', authors: ['Auth'] },
  { asin: 'A2', title: 'Book B', authors: ['Auth'] },
]

function mountModal() {
  return mount(AddSelectedBooksModal, {
    props: { visible: true, books, seriesName: 'My Series', region: 'us' },
    global: { stubs: ['RootFolderSelect'] },
  })
}

describe('AddSelectedBooksModal', () => {
  beforeEach(() => mockAddToLibrary.mockReset())

  it('preselects all supplied books and shows the count', async () => {
    const wrapper = mountModal()
    await flushPromises()
    expect(wrapper.text()).toContain('Book A')
    expect(wrapper.text()).toContain('Book B')
    expect(wrapper.find('.btn-primary').text()).toContain('Add 2 books')
  })

  it('adds selected books and treats a 409 as a benign skip (not a failure)', async () => {
    mockAddToLibrary
      .mockResolvedValueOnce({ audiobook: { id: 1 } })
      .mockRejectedValueOnce(Object.assign(new Error('exists'), { status: 409 }))

    const wrapper = mountModal()
    await flushPromises()
    await wrapper.find('.btn-primary').trigger('click')
    await flushPromises()

    expect(mockAddToLibrary).toHaveBeenCalledTimes(2)
    // autoSearch defaults OFF (don't fire a bulk of searches unintentionally).
    expect(mockAddToLibrary.mock.calls[0][1]).toMatchObject({ autoSearch: false, monitored: true })
    const done = wrapper.emitted('done')?.[0]?.[0] as {
      added: number
      skipped: number
      failed: number
    }
    expect(done).toEqual({ added: 1, skipped: 1, failed: 0 })
  })

  it('does not add deselected books', async () => {
    mockAddToLibrary.mockResolvedValue({ audiobook: { id: 1 } })
    const wrapper = mountModal()
    await flushPromises()

    // Uncheck the first book.
    await wrapper.findAll('.book-row input[type="checkbox"]')[0].setValue(false)
    await wrapper.find('.btn-primary').trigger('click')
    await flushPromises()

    expect(mockAddToLibrary).toHaveBeenCalledTimes(1)
    expect(mockAddToLibrary.mock.calls[0][0]).toMatchObject({ asin: 'A2' })
  })
})
