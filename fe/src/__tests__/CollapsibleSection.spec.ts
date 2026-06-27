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
import { describe, it, expect, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import CollapsibleSection from '@/components/ui/CollapsibleSection.vue'
import type { Audiobook } from '@/types'

describe('CollapsibleSection', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it('renders the title', () => {
    const w = mount(CollapsibleSection, { props: { title: 'Continue Listening', sectionKey: 'continue' } })
    expect(w.text()).toContain('Continue Listening')
  })

  it('shows count badge when count prop is provided', () => {
    const w = mount(CollapsibleSection, { props: { title: 'Library', count: 42, sectionKey: 'library' } })
    expect(w.find('.section-count').text()).toBe('42')
  })

  it('omits count badge when count prop is absent', () => {
    const w = mount(CollapsibleSection, { props: { title: 'Library', sectionKey: 'library' } })
    expect(w.find('.section-count').exists()).toBe(false)
  })

  it('renders slot content when expanded', () => {
    const w = mount(CollapsibleSection, {
      props: { title: 'Test', sectionKey: 'test' },
      slots: { default: '<p class="inner">hello</p>' },
    })
    expect(w.find('.inner').isVisible()).toBe(true)
  })

  it('hides slot content after toggle (collapsed)', async () => {
    const w = mount(CollapsibleSection, {
      props: { title: 'Test', sectionKey: 'test' },
      slots: { default: '<p class="inner">hello</p>' },
      attachTo: document.body,
    })
    await w.find('.section-header').trigger('click')
    expect(w.find('.inner').isVisible()).toBe(false)
    w.unmount()
  })

  it('sets aria-expanded to false when collapsed', async () => {
    const w = mount(CollapsibleSection, { props: { title: 'Test', sectionKey: 'test' } })
    await w.find('.section-header').trigger('click')
    expect(w.find('.section-header').attributes('aria-expanded')).toBe('false')
  })

  it('aria-controls matches the content region id', () => {
    const w = mount(CollapsibleSection, { props: { title: 'Test', sectionKey: 'library' } })
    const btnId = w.find('.section-header').attributes('aria-controls')
    expect(btnId).toBe('section-content-library')
    expect(w.find('#section-content-library').exists()).toBe(true)
  })

  it('persists collapsed state to localStorage', async () => {
    const w = mount(CollapsibleSection, { props: { title: 'Test', sectionKey: 'finished' } })
    await w.find('.section-header').trigger('click') // collapse
    expect(localStorage.getItem('books.section.finished')).toBe('false')
  })

  it('reads collapsed state from localStorage on mount', () => {
    localStorage.setItem('books.section.continue', 'false')
    const w = mount(CollapsibleSection, {
      props: { title: 'Test', sectionKey: 'continue' },
      slots: { default: '<p class="inner">x</p>' },
      attachTo: document.body,
    })
    expect(w.find('.inner').isVisible()).toBe(false)
    w.unmount()
  })
})

// --- Section bucketing logic (pure filter predicates) ---
// These mirror the computed expressions in AudiobooksView to catch regressions.

describe('section bucketing predicates', () => {
  const books: Audiobook[] = [
    { id: 1, title: 'In Progress', playbackPositionSeconds: 600, finished: false },
    { id: 2, title: 'Finished', playbackPositionSeconds: 36000, finished: true },
    { id: 3, title: 'Unstarted', playbackPositionSeconds: 0, finished: false },
    { id: 4, title: 'No pos field', finished: false },
  ]

  const continueFilter = (b: Audiobook) =>
    (b.playbackPositionSeconds ?? 0) > 0 && !b.finished

  const finishedFilter = (b: Audiobook) => b.finished === true

  it('continueListeningBooks includes only in-progress books', () => {
    const result = books.filter(continueFilter)
    expect(result.map((b) => b.id)).toEqual([1])
  })

  it('finishedBooks includes only finished books', () => {
    const result = books.filter(finishedFilter)
    expect(result.map((b) => b.id)).toEqual([2])
  })

  it('unstarted books appear in neither section', () => {
    const unstarted = books.filter((b) => !continueFilter(b) && !finishedFilter(b))
    expect(unstarted.map((b) => b.id)).toEqual([3, 4])
  })

  it('finished book is not in continueListeningBooks even if it has a position', () => {
    // book id 2 has pos=36000 and finished=true — should NOT be in CL
    const cl = books.filter(continueFilter)
    expect(cl.find((b) => b.id === 2)).toBeUndefined()
  })
})
