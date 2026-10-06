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
import { createPinia, setActivePinia } from 'pinia'
import type { QualityProfile } from '@/types'

/**
 * The "Enable Quality Upgrades" checkbox used to be local state that the save handler turned into
 * a blank cutoff. Saving with it off therefore destroyed the cutoff, and the server had no way to
 * tell "upgrades off" from "no cutoff chosen". It is now bound to the profile's own
 * upgradeAllowed field.
 */
describe('QualityProfileFormModal upgrade toggle', () => {
  const ladder = [
    {
      quality: 'AAC 320kbps',
      allowed: true,
      priority: 0,
      codec: 'AAC',
      bitrate: 320,
      isLossless: false,
    },
    {
      quality: 'AAC 256kbps',
      allowed: true,
      priority: 1,
      codec: 'AAC',
      bitrate: 256,
      isLossless: false,
    },
    {
      quality: 'MP3 320kbps',
      allowed: true,
      priority: 2,
      codec: 'MP3',
      bitrate: 320,
      isLossless: false,
    },
  ]

  const buildProfile = (overrides: Partial<QualityProfile>): QualityProfile => ({
    id: 7,
    name: 'Test profile',
    qualities: [...ladder],
    cutoffQuality: 'AAC 256kbps',
    ...overrides,
  })

  /**
   * Mounts with no profile and then hands one over, which is what QualityProfilesTab does: the
   * modal is always mounted and `editingQualityProfile` starts null
   * (fe/src/views/settings/QualityProfilesTab.vue:253-255).
   */
  const mountModal = async (profile: QualityProfile) => {
    const Modal = (await import('@/components/settings/QualityProfileFormModal.vue')).default
    const wrapper = mount(Modal, { props: { visible: true, profile: null } })
    await wrapper.setProps({ profile })
    await wrapper.vm.$nextTick()
    return wrapper
  }

  const savedProfile = (wrapper: { emitted: (name: string) => unknown[][] | undefined }) => {
    const events = wrapper.emitted('save')
    expect(events).toBeTruthy()
    return events![0][0] as QualityProfile
  }

  it('reads the checkbox from upgradeAllowed rather than from the cutoff', async () => {
    const wrapper = await mountModal(buildProfile({ upgradeAllowed: false }))

    // The cutoff select and its hint are both behind the checkbox, so their absence is how the
    // rendered state reports the checkbox is off.
    expect(wrapper.find('#cutoff-quality').exists()).toBe(false)
  })

  it('shows the cutoff select when upgrades are on', async () => {
    const wrapper = await mountModal(buildProfile({ upgradeAllowed: true }))

    expect(wrapper.find('#cutoff-quality').exists()).toBe(true)
  })

  it('saves with upgrades off and keeps the cutoff it was given', async () => {
    const wrapper = await mountModal(buildProfile({ upgradeAllowed: false }))

    await (wrapper.vm as unknown as { handleSubmit: () => void }).handleSubmit()

    const saved = savedProfile(wrapper)
    expect(saved.upgradeAllowed).toBe(false)
    expect(saved.cutoffQuality).toBe('AAC 256kbps')
  })

  it('saves with upgrades on and the cutoff intact', async () => {
    const wrapper = await mountModal(buildProfile({ upgradeAllowed: true }))

    await (wrapper.vm as unknown as { handleSubmit: () => void }).handleSubmit()

    const saved = savedProfile(wrapper)
    expect(saved.upgradeAllowed).toBe(true)
    expect(saved.cutoffQuality).toBe('AAC 256kbps')
  })

  it('refuses to save with upgrades on and no cutoff chosen', async () => {
    const wrapper = await mountModal(buildProfile({ upgradeAllowed: true, cutoffQuality: '' }))

    await (wrapper.vm as unknown as { handleSubmit: () => void }).handleSubmit()

    expect(wrapper.emitted('save')).toBeUndefined()
  })

  it('takes upgrades off from a blank cutoff when the server sent no flag', async () => {
    const legacy = buildProfile({ cutoffQuality: '' })
    delete legacy.upgradeAllowed

    const wrapper = await mountModal(legacy)
    await (wrapper.vm as unknown as { handleSubmit: () => void }).handleSubmit()

    const saved = savedProfile(wrapper)
    expect(saved.upgradeAllowed).toBe(false)
  })

  it('leaves upgrades on for a legacy profile that names a cutoff', async () => {
    const legacy = buildProfile({})
    delete legacy.upgradeAllowed

    const wrapper = await mountModal(legacy)
    await (wrapper.vm as unknown as { handleSubmit: () => void }).handleSubmit()

    expect(savedProfile(wrapper).upgradeAllowed).toBe(true)
  })

  it('refuses to save a cutoff naming a quality that is no longer enabled', async () => {
    const withDisabledCutoffRung = buildProfile({
      upgradeAllowed: true,
      qualities: [{ ...ladder[0] }, { ...ladder[1], allowed: false }, { ...ladder[2] }],
    })

    const wrapper = await mountModal(withDisabledCutoffRung)
    await (wrapper.vm as unknown as { handleSubmit: () => void }).handleSubmit()

    // The cutoff is still 'AAC 256kbps' and that rung is now disallowed, which is the second of
    // the two guards in handleSubmit and the same shape the server refuses.
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  it('saves that same profile once upgrades are turned off', async () => {
    const withDisabledCutoffRung = buildProfile({
      upgradeAllowed: false,
      qualities: [{ ...ladder[0] }, { ...ladder[1], allowed: false }, { ...ladder[2] }],
    })

    const wrapper = await mountModal(withDisabledCutoffRung)
    await (wrapper.vm as unknown as { handleSubmit: () => void }).handleSubmit()

    // The control for the test above: same profile, same disallowed cutoff, and it saves, so what
    // refused it was the upgrade flag rather than anything else about the payload.
    const saved = savedProfile(wrapper)
    expect(saved.upgradeAllowed).toBe(false)
    expect(saved.cutoffQuality).toBe('AAC 256kbps')
  })

  it('offers no selectable cutoff option that the server would refuse', async () => {
    const wrapper = await mountModal(buildProfile({ upgradeAllowed: true }))

    const options = wrapper.find('#cutoff-quality').findAll('option')
    expect(options.length).toBeGreaterThan(1)

    // The empty entry is a prompt, not a choice. It used to say "No Cutoff (Always Upgrade)" and
    // be selectable, and picking it could only ever produce a validation error.
    const empty = options.filter((option) => option.attributes('value') === '')
    expect(empty).toHaveLength(1)
    expect(empty[0].attributes('disabled')).toBeDefined()
    expect(empty[0].text()).not.toContain('Always Upgrade')
  })
})

/**
 * The profile list draws "Upgrade until X" and a scissors marker from the cutoff alone. Now that a
 * profile can keep its cutoff while not upgrading, the cutoff by itself no longer says the profile
 * is going to upgrade until it, and the list would otherwise claim it is.
 */
describe('QualityProfilesTab cutoff marker', () => {
  const profile = (overrides: Partial<QualityProfile>): QualityProfile => ({
    id: 1,
    name: 'Listed profile',
    cutoffQuality: 'AAC 256kbps',
    qualities: [
      { quality: 'AAC 320kbps', allowed: true, priority: 0, codec: 'AAC', bitrate: 320 },
      { quality: 'AAC 256kbps', allowed: true, priority: 1, codec: 'AAC', bitrate: 256 },
    ],
    ...overrides,
  })

  const renderWith = async (listed: QualityProfile) => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const api = await import('@/services/api')
    vi.spyOn(api, 'getQualityProfiles').mockResolvedValue([listed] as never)

    const Tab = (await import('@/views/settings/QualityProfilesTab.vue')).default
    const wrapper = mount(Tab, { global: { plugins: [pinia] } })
    await new Promise((resolve) => setTimeout(resolve, 0))
    await wrapper.vm.$nextTick()
    return wrapper
  }

  it('does not claim a profile upgrades when upgrades are off', async () => {
    const wrapper = await renderWith(profile({ upgradeAllowed: false }))

    expect(wrapper.find('.quality-subtitle').exists()).toBe(false)
    expect(wrapper.find('.quality-badge.is-cutoff').exists()).toBe(false)
  })

  it('still marks the cutoff when upgrades are on', async () => {
    const wrapper = await renderWith(profile({ upgradeAllowed: true }))

    expect(wrapper.find('.quality-subtitle').text()).toContain('AAC 256kbps')
    expect(wrapper.find('.quality-badge.is-cutoff').exists()).toBe(true)
  })

  it('still marks the cutoff for a profile from a server without the flag', async () => {
    const legacy = profile({})
    delete legacy.upgradeAllowed

    const wrapper = await renderWith(legacy)

    expect(wrapper.find('.quality-subtitle').exists()).toBe(true)
  })
})
