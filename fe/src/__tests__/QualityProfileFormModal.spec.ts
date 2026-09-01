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
import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success: vi.fn(), error: vi.fn(), info: vi.fn() }),
}))

const mountModal = async (
  preferNewerReleases: boolean,
  qualities?: Array<{ quality: string; allowed: boolean }>,
) => {
  const QualityProfileFormModal = (
    await import('@/components/settings/QualityProfileFormModal.vue')
  ).default

  const wrapper = mount(QualityProfileFormModal, {
    props: {
      visible: true,
      profile: {
        id: 1,
        name: 'Test Profile',
        preferNewerReleases,
        maximumAge: 30,
        qualities: qualities ?? [],
      } as unknown as import('@/types').QualityProfile,
    },
    global: { stubs: { Teleport: true } },
  })
  await wrapper.vm.$nextTick()
  await wrapper.vm.$nextTick()
  return wrapper
}

describe('QualityProfileFormModal maximum age', () => {
  it('offers the Maximum Age input when Prefer newer releases is off', async () => {
    // Maximum Age is a hard reject applied by SearchResultScorer whenever it is above zero,
    // and the scorer never reads PreferNewerReleases. Hiding the input behind the checkbox
    // hid a filter that stayed switched on.
    const wrapper = await mountModal(false)

    expect(wrapper.find('#maximumAge').exists()).toBe(true)
    expect((wrapper.find('#maximumAge').element as HTMLInputElement).value).toBe('30')

    wrapper.unmount()
  })

  it('offers the Maximum Age input when Prefer newer releases is on', async () => {
    // The control. The input has to be present in both states, or this pair would pass
    // against a form that had simply inverted the condition.
    const wrapper = await mountModal(true)

    expect(wrapper.find('#maximumAge').exists()).toBe(true)

    wrapper.unmount()
  })
})

describe('QualityProfileFormModal quality initialisation', () => {
  it('loads the saved qualities when an existing profile is opened', async () => {
    // The watch that copies the profile into the form runs with immediate: true, so it calls
    // initializeQualitiesFromProfile during setup. While that was a const arrow declared
    // further down the file it threw ReferenceError there, Vue swallowed it, and the form
    // opened with nothing selected. Saving was then refused by the "select at least one
    // quality" guard until every quality was picked again by hand.
    const wrapper = await mountModal(false, [
      { quality: 'MP3 128kbps', allowed: true },
      { quality: 'MP3 320kbps', allowed: true },
    ])

    const vm = wrapper.vm as unknown as { qualityItems: Array<{ enabled: boolean }> }
    expect(vm.qualityItems.length).toBeGreaterThan(0)
    expect(vm.qualityItems.some((item) => item.enabled)).toBe(true)

    wrapper.unmount()
  })

  it('leaves the qualities empty for a profile that has none', async () => {
    // The control. Without it, the test above would also pass against an implementation
    // that populated the list from somewhere other than the profile.
    const wrapper = await mountModal(false, [])

    const vm = wrapper.vm as unknown as { qualityItems: Array<{ enabled: boolean }> }
    expect(vm.qualityItems).toHaveLength(0)

    wrapper.unmount()
  })
})
