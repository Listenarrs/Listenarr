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
const setLibraryIssueIgnored = vi.hoisted(() => vi.fn())
vi.mock('@/services/api', () => ({ apiService: { getLibraryIssues, setLibraryIssueIgnored } }))
const showConfirm = vi.hoisted(() => vi.fn())
vi.mock('@/composables/confirmService', () => ({ showConfirm }))
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
    window.localStorage.clear()
    setLibraryIssueIgnored.mockResolvedValue({})
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

  it('stays on the page after the last repair, with one row saying no issues were found', async () => {
    getLibraryIssues.mockResolvedValue([issues[0]])
    const wrapper = mountView()
    await flushPromises()

    getLibraryIssues.mockResolvedValue([])
    wrapper.findComponent(RepairStub).vm.$emit('done')
    await flushPromises()

    expect(routerReplace).not.toHaveBeenCalled()
    const empty = wrapper.get('.issues-list [data-testid="issues-empty"]')
    expect(empty.classes()).toContain('issues-row')
    expect(empty.text()).toContain('No issues found.')
    // Same toolbar, nothing to select
    expect(
      (wrapper.get('[data-testid="issues-select-all"]').element as HTMLInputElement).disabled,
    ).toBe(true)
    expect(
      wrapper.get('[data-testid="issues-repair-selected"]').attributes('disabled'),
    ).toBeDefined()
    expect(wrapper.find('[data-testid="issues-show-ignored"]').exists()).toBe(false)
  })

  it('ignores a book after confirmation, then lists it again with the filter', async () => {
    getLibraryIssues.mockResolvedValue(issues)
    showConfirm.mockResolvedValue(true)
    const wrapper = mountView()
    await flushPromises()
    expect(getLibraryIssues).toHaveBeenCalledWith(true)

    await wrapper.get('[data-testid="issues-row-9"] [data-testid="issues-ignore"]').trigger('click')
    await flushPromises()

    expect(showConfirm).toHaveBeenCalledWith(
      expect.stringContaining('"dfh"'),
      'Ignore this book',
      expect.objectContaining({ confirmText: 'Ignore' }),
    )
    expect(setLibraryIssueIgnored).toHaveBeenCalledWith(9, true)
    expect(wrapper.find('[data-testid="issues-row-9"]').exists()).toBe(false)

    await wrapper.get('[data-testid="issues-show-ignored"]').trigger('click')
    expect(wrapper.get('[data-testid="issues-row-9"]').text()).toContain('Ignored')

    await wrapper
      .get('[data-testid="issues-row-9"] [data-testid="issues-unignore"]')
      .trigger('click')
    await flushPromises()
    expect(setLibraryIssueIgnored).toHaveBeenCalledWith(9, false)
    expect(
      wrapper.find('[data-testid="issues-row-9"] [data-testid="issues-ignore"]').exists(),
    ).toBe(true)
  })

  it('does not ignore a book when the confirmation is cancelled', async () => {
    getLibraryIssues.mockResolvedValue(issues)
    showConfirm.mockResolvedValue(false)
    const wrapper = mountView()
    await flushPromises()

    await wrapper.get('[data-testid="issues-row-9"] [data-testid="issues-ignore"]').trigger('click')
    await flushPromises()

    expect(setLibraryIssueIgnored).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="issues-row-9"]').exists()).toBe(true)
  })

  it.each([
    [true, false],
    [false, true],
  ])(
    'asks whether an ignored book stays ignored before repairing it (keep: %s)',
    async (keep, unignored) => {
      getLibraryIssues.mockResolvedValue([issues[0], { ...issues[1]!, ignored: true }])
      showConfirm.mockResolvedValue(keep)
      const wrapper = mountView()
      await flushPromises()
      await wrapper.get('[data-testid="issues-show-ignored"]').trigger('click')

      await wrapper
        .get('[data-testid="issues-row-9"] [data-testid="issues-repair"]')
        .trigger('click')
      await flushPromises()

      expect(showConfirm).toHaveBeenCalledWith(
        expect.stringContaining('ignored'),
        'Repair ignored books',
        expect.objectContaining({ confirmText: 'Keep ignored', cancelText: 'Stop ignoring' }),
      )
      expect(setLibraryIssueIgnored.mock.calls.some(([id, v]) => id === 9 && v === false)).toBe(
        unignored,
      )
      expect(wrapper.get('[data-testid="repair-modal"]').attributes('data-ids')).toBe('9')
    },
  )

  it('stays available while ignored books exist, with nothing left to repair', async () => {
    getLibraryIssues.mockResolvedValue([{ ...issues[1]!, ignored: true }])
    const wrapper = mountView()
    await flushPromises()

    expect(routerReplace).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="issues-empty"]').text()).toContain('1 ignored book is hidden')
  })

  it('switches the ignored books on and off next to Repair, even when only ignored books are left', async () => {
    getLibraryIssues.mockResolvedValue([{ ...issues[1]!, ignored: true }])
    const wrapper = mountView()
    await flushPromises()

    const toggle = wrapper.get('[data-testid="issues-show-ignored"]')
    expect(toggle.attributes('role')).toBe('switch')
    expect(toggle.attributes('aria-checked')).toBe('false')
    expect(toggle.text()).toContain('Show ignored books (1)')
    expect(
      wrapper.get('[data-testid="issues-repair-selected"]').attributes('disabled'),
    ).toBeDefined()

    await toggle.trigger('click')
    expect(toggle.attributes('aria-checked')).toBe('true')
    expect(toggle.classes()).toContain('active')
    // Now listed, the book can be selected and repaired from the same line
    const toolbar = wrapper.get('.issues-toolbar')
    expect(toolbar.find('[data-testid="issues-show-ignored"]').exists()).toBe(true)
    expect(toolbar.find('[data-testid="issues-repair-selected"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="issues-row-9"]').exists()).toBe(true)

    await toggle.trigger('click')
    expect(wrapper.find('[data-testid="issues-row-9"]').exists()).toBe(false)
  })

  it('keeps the choice to show ignored books for the next visit', async () => {
    getLibraryIssues.mockResolvedValue([issues[0], { ...issues[1]!, ignored: true }])
    const first = mountView()
    await flushPromises()
    await first.get('[data-testid="issues-show-ignored"]').trigger('click')
    first.unmount()

    const back = mountView()
    await flushPromises()
    expect(back.get('[data-testid="issues-show-ignored"]').attributes('aria-checked')).toBe('true')
    expect(back.find('[data-testid="issues-row-9"]').exists()).toBe(true)
  })
})
