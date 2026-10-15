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
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent } from 'vue'

const getLibraryIssues = vi.hoisted(() => vi.fn())
vi.mock('@/services/api', () => ({ apiService: { getLibraryIssues } }))
vi.mock('@/composables/useProtectedImages', () => ({
  useProtectedImages: () => ({ getProtectedImageSrc: () => '/placeholder.svg' }),
}))
const routerReplace = vi.hoisted(() => vi.fn())
vi.mock('vue-router', () => ({
  useRouter: () => ({ replace: routerReplace }),
  RouterLink: { props: ['to'], template: '<a><slot /></a>' },
}))

import IssuesView from '@/views/system/IssuesView.vue'
import type { LibraryIssue } from '@/types'

const RepairStub = defineComponent({
  name: 'RepairBooksModal',
  props: { visible: Boolean, audiobookIds: { type: Array, default: () => [] } },
  emits: ['close', 'done', 'organize'],
  template:
    '<div data-testid="repair-modal" :data-ids="audiobookIds.join(\',\')" v-if="visible" />',
})

const issues: LibraryIssue[] = [
  {
    id: 7,
    title: 'Primal Hunter 4',
    authors: ['Zogarth'],
    asin: 'B0BK9W82PQ',
    issues: ['missing_region'],
  },
  { id: 9, title: 'dfh', asin: 'erg', issues: ['invalid_asin'] },
]

function mountView() {
  setActivePinia(createPinia())
  return mount(IssuesView, {
    global: { stubs: { RepairBooksModal: RepairStub, RenamePreviewModal: true } },
  })
}

describe('IssuesView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('lists the books with their issues', async () => {
    getLibraryIssues.mockResolvedValue(issues)
    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.get('[data-testid="issues-row-7"]').text()).toContain('No region')
    expect(wrapper.get('[data-testid="issues-row-7"]').text()).toContain('B0BK9W82PQ')
    expect(wrapper.get('[data-testid="issues-row-9"]').text()).toContain('Invalid ASIN')
  })

  it('opens Repair for one book from its row', async () => {
    getLibraryIssues.mockResolvedValue(issues)
    const wrapper = mountView()
    await flushPromises()

    await wrapper.get('[data-testid="issues-row-9"] input[type="checkbox"]').setValue(true)
    expect(wrapper.get('[data-testid="issues-repair-selected"]').text()).toContain(
      'Repair Selected Book (1)',
    )
    await wrapper.get('[data-testid="issues-row-9"] [data-testid="issues-repair"]').trigger('click')

    expect(wrapper.get('[data-testid="repair-modal"]').attributes('data-ids')).toBe('9')
  })

  it('selects every book and repairs them together', async () => {
    getLibraryIssues.mockResolvedValue(issues)
    const wrapper = mountView()
    await flushPromises()
    const repairBooks = () => wrapper.get('[data-testid="issues-repair-selected"]')
    expect(repairBooks().attributes('disabled')).toBeDefined()

    await wrapper.get('[data-testid="issues-select-all"]').setValue(true)
    await repairBooks().trigger('click')

    expect(wrapper.get('[data-testid="repair-modal"]').attributes('data-ids')).toBe('7,9')
    expect(repairBooks().text()).toContain('Repair Selected Books (2)')
  })

  it('repeats the repair button under a list taller than the screen', async () => {
    getLibraryIssues.mockResolvedValue(issues)
    const rect = vi
      .spyOn(HTMLElement.prototype, 'getBoundingClientRect')
      .mockReturnValue({ bottom: window.innerHeight + 500 } as DOMRect)
    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="issues-repair-selected-bottom"]').exists()).toBe(true)

    rect.mockReturnValue({ bottom: 100 } as DOMRect)
    window.dispatchEvent(new Event('resize'))
    await flushPromises()
    expect(wrapper.find('[data-testid="issues-repair-selected-bottom"]').exists()).toBe(false)
    rect.mockRestore()
  })

  it('goes back to the books when there is nothing to repair, including after the last repair', async () => {
    getLibraryIssues.mockResolvedValue([issues[0]])
    const wrapper = mountView()
    await flushPromises()
    expect(routerReplace).not.toHaveBeenCalled()

    getLibraryIssues.mockResolvedValue([])
    wrapper.findComponent(RepairStub).vm.$emit('done')
    await flushPromises()

    expect(routerReplace).toHaveBeenCalledWith({ path: '/audiobooks', query: { group: 'books' } })
  })
})
