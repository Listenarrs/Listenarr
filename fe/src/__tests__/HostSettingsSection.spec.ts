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
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'

const load = async () => (await import('@/components/settings/HostSettingsSection.vue')).default

describe('HostSettingsSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('gives the input the section class the styling hangs off', async () => {
    // Scoped styles do not cascade between sibling sections, so an input with no
    // class of its own renders with browser defaults inside a card that is itself
    // unstyled. That is what shipped first: this asserts the hook exists, though
    // whether the rules look right is still a thing to check with eyes.
    const wrapper = mount(await load(), {
      props: { startupConfig: { urlBase: '', port: 4545 } },
    })

    expect(wrapper.get('#urlBase').classes()).toContain('url-base-input')
    expect(wrapper.find('.form-body').exists()).toBe(true)
  })

  it('shows the configured url base', async () => {
    const wrapper = mount(await load(), {
      props: { startupConfig: { urlBase: '/listenarr', port: 4545 } },
    })

    expect((wrapper.get('#urlBase').element as HTMLInputElement).value).toBe('/listenarr')
  })

  it('renders an empty field when no url base is set', async () => {
    const wrapper = mount(await load(), { props: { startupConfig: { port: 4545 } } })

    expect((wrapper.get('#urlBase').element as HTMLInputElement).value).toBe('')
  })

  it('emits the new url base without disturbing the rest of the config', async () => {
    // The save path spreads the whole startup config, so dropping a sibling
    // field here would silently erase it from config.json on the next save.
    const wrapper = mount(await load(), {
      props: { startupConfig: { urlBase: '', port: 4545, apiKey: 'unchanged' } },
    })

    const input = wrapper.get('#urlBase')
    ;(input.element as HTMLInputElement).value = '/listenarr'
    await input.trigger('change')

    const emitted = wrapper.emitted('update:startupConfig')
    expect(emitted).toHaveLength(1)
    expect(emitted![0][0]).toEqual({ urlBase: '/listenarr', port: 4545, apiKey: 'unchanged' })
  })

  it('trims surrounding whitespace before emitting', async () => {
    const wrapper = mount(await load(), { props: { startupConfig: {} } })

    const input = wrapper.get('#urlBase')
    ;(input.element as HTMLInputElement).value = '  /listenarr  '
    await input.trigger('change')

    expect(wrapper.emitted('update:startupConfig')![0][0]).toEqual({ urlBase: '/listenarr' })
  })

  it('warns that an absolute URL is not a path', async () => {
    // NormalizeUrlBase treats a full URL as unusable and serves at the site
    // root, so accepting one silently would save a value that does nothing.
    const wrapper = mount(await load(), {
      props: { startupConfig: { urlBase: 'https://example.com/listenarr' } },
    })

    expect(wrapper.get('[role="alert"]').text()).toContain('Must be a path')
  })

  it('does not warn about an ordinary path or an empty value', async () => {
    for (const urlBase of ['/listenarr', '']) {
      const wrapper = mount(await load(), { props: { startupConfig: { urlBase } } })
      expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    }
  })

  it('tolerates a null startup config', async () => {
    const wrapper = mount(await load(), { props: { startupConfig: null } })

    expect((wrapper.get('#urlBase').element as HTMLInputElement).value).toBe('')
  })
})
