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
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createMemoryHistory, createRouter } from 'vue-router'
import { PhGear } from '@phosphor-icons/vue'
import HeaderMenu from '@/components/ui/HeaderMenu.vue'
import type { HeaderMenuItem } from '@/components/ui/HeaderMenu.vue'

const READ_KEY = 'listenarr.test.read'

function item(overrides: Partial<HeaderMenuItem> = {}): HeaderMenuItem {
  return {
    id: 'job-1',
    title: 'Scanning A for unmatched files',
    message: 'Library Import',
    icon: 'ph ph-folder-open',
    active: true,
    spinner: true,
    ...overrides,
  }
}

describe('HeaderMenu', () => {
  let wrapper: VueWrapper | undefined

  beforeEach(() => sessionStorage.clear())
  afterEach(() => {
    wrapper?.unmount()
    wrapper = undefined
  })

  async function mountMenu(items: HeaderMenuItem[], extra: Record<string, unknown> = {}) {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', component: { template: '<div />' } },
        { path: '/jobs', name: 'jobs', component: { template: '<div />' } },
        { path: '/library-import', name: 'library-import', component: { template: '<div />' } },
      ],
    })
    await router.push('/')
    await router.isReady()
    wrapper = mount(HeaderMenu, {
      props: {
        label: 'Jobs',
        icon: PhGear,
        items,
        readStorageKey: READ_KEY,
        footer: { to: { name: 'jobs' }, label: 'View jobs' },
        emptyText: 'No jobs',
        ...extra,
      },
      global: { plugins: [router] },
      attachTo: document.body,
    })
    return { wrapper, router }
  }

  const open = (w: VueWrapper) => w.find('.nav-btn').trigger('click')
  const badge = (w: VueWrapper) => {
    const element = w.find('.notification-badge')
    return element.exists() ? element.text() : null
  }

  it('lists its items, with a spinner for a running one and no dismiss button', async () => {
    const { wrapper: w } = await mountMenu([
      item(),
      item({ id: 'job-2', title: 'Done', active: false, spinner: false }),
    ])
    await open(w)

    const rows = w.findAll('.notification-item')
    expect(w.find('.dropdown-header').text()).toContain('Jobs')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.find('.notif-icon .ph-spin').exists()).toBe(true)
    expect(rows[0]!.find('.dismiss-btn').exists()).toBe(false)
    expect(rows[1]!.find('.ph-spin').exists()).toBe(false)
    expect(rows[1]!.find('.dismiss-btn').exists()).toBe(true)
  })

  it('shows a progress bar for an item with a percentage', async () => {
    const { wrapper: w } = await mountMenu([
      item({ spinner: false, progress: 42, showProgressPercentage: true }),
    ])
    await open(w)

    expect(w.find('.progress-fill').exists()).toBe(true)
    expect(w.text()).toContain('42%')
  })

  it('leads to where the item was started from, and closes', async () => {
    const { wrapper: w, router } = await mountMenu([
      item({ to: { name: 'library-import', query: { rootFolder: '7' } } }),
    ])
    await open(w)

    await w.find('a.notif-title').trigger('click')
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(router.currentRoute.value.fullPath).toBe('/library-import?rootFolder=7')
    expect(w.find('.notification-dropdown').exists()).toBe(false)
  })

  it('asks to dismiss an item and to clear the list', async () => {
    const { wrapper: w } = await mountMenu([item({ active: false, spinner: false })])
    await open(w)

    await w.find('.dismiss-btn').trigger('click')
    await w.find('.clear-btn').trigger('click')

    expect(w.emitted('dismiss')).toEqual([['job-1']])
    expect(w.emitted('clear')).toHaveLength(1)
  })

  it('shows its empty text and its footer link', async () => {
    const { wrapper: w, router } = await mountMenu([])
    await open(w)

    expect(w.find('.notification-empty').text()).toBe('No jobs')
    await w.find('.view-all-link').trigger('click')
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(router.currentRoute.value.name).toBe('jobs')
    expect(w.find('.notification-dropdown').exists()).toBe(false)
  })

  it('closes on a click outside', async () => {
    const { wrapper: w } = await mountMenu([item()])
    await open(w)

    document.body.click()
    await w.vm.$nextTick()

    expect(w.find('.notification-dropdown').exists()).toBe(false)
  })

  it('spins its icon when asked (a job is running)', async () => {
    const { wrapper: w } = await mountMenu([item()], { iconSpinning: true })
    expect(w.find('.nav-btn .header-menu-icon').classes()).toContain('ph-spin')

    await w.setProps({ iconSpinning: false })
    expect(w.find('.nav-btn .header-menu-icon').classes()).not.toContain('ph-spin')
  })

  describe('unread count', () => {
    it('counts unread items, and opening reads them', async () => {
      const { wrapper: w } = await mountMenu([item(), item({ id: 'job-2' })])
      expect(badge(w)).toBe('2')

      await open(w)

      expect(badge(w)).toBeNull()
    })

    it('counts an item again when its title changes, not when only its message does', async () => {
      const { wrapper: w } = await mountMenu([item()])
      await open(w)
      await open(w)

      await w.setProps({ items: [item({ message: 'started at 10:41' })] })
      expect(badge(w)).toBeNull()

      await w.setProps({ items: [item({ title: 'Scan of A finished', active: false })] })
      expect(badge(w)).toBe('1')
    })

    it('reads what arrives while it is open', async () => {
      const { wrapper: w } = await mountMenu([])
      await open(w)

      await w.setProps({ items: [item()] })
      await w.vm.$nextTick()
      await open(w)

      expect(badge(w)).toBeNull()
    })

    it('remembers what was read for the browser session, under its own key', async () => {
      const first = await mountMenu([item()])
      await open(first.wrapper)
      first.wrapper.unmount()

      const { wrapper: same } = await mountMenu([item()])
      expect(badge(same)).toBeNull()
      same.unmount()

      const { wrapper: other } = await mountMenu([item()], { readStorageKey: 'listenarr.other' })
      expect(badge(other)).toBe('1')
    })
  })
})
