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
import type { ComponentPublicInstance } from 'vue'
import { mount } from '@vue/test-utils'

import PasswordInput from '@/components/form/PasswordInput.vue'
import Checkbox from '@/components/form/Checkbox.vue'

describe('AuthenticationSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('renders the admin credential inputs even when authEnabled is false', async () => {
    // Regression guard: the admin form must be visible while auth is disabled
    // so a user can configure credentials before turning the login screen on.
    // Previously this was gated by `v-if="authEnabledComputed"`, which made
    // first-time setup require enabling auth (and the login screen) before
    // any UI affordance for credentials existed — a lockout.
    const { default: AuthenticationSection } =
      await import('@/components/settings/AuthenticationSection.vue')
    const wrapper = mount(AuthenticationSection, {
      props: { settings: { adminUsername: '', adminPassword: '' }, authEnabled: false },
      global: { components: { PasswordInput } },
    })

    expect(wrapper.find('input[type="text"][placeholder="Admin username"]').exists()).toBe(true)
    expect(wrapper.findComponent(PasswordInput).exists()).toBe(true)
  })

  it('emits update:authEnabled when checkbox toggled', async () => {
    const { default: AuthenticationSection } =
      await import('@/components/settings/AuthenticationSection.vue')
    const wrapper = mount(AuthenticationSection, {
      props: { settings: { adminUsername: 'admin', adminPassword: '' }, authEnabled: false },
      global: { components: { PasswordInput, Checkbox } },
    })

    const checkbox = wrapper.find('input[type="checkbox"]')
    expect(checkbox.exists()).toBe(true)
    await checkbox.setValue(true)

    expect(wrapper.emitted()['update:authEnabled']).toBeTruthy()
    expect(wrapper.emitted()['update:authEnabled']![0]).toEqual([true])
  })

  it('emits update:settings when username or password changes', async () => {
    const { default: AuthenticationSection } =
      await import('@/components/settings/AuthenticationSection.vue')
    const wrapper = mount(AuthenticationSection, {
      props: { settings: { adminUsername: 'admin', adminPassword: '' }, authEnabled: true },
      global: { components: { PasswordInput } },
    })

    const username = wrapper.find('input[type="text"]')
    await username.setValue('newadmin')

    // Last emitted update:settings should reflect adminUsername change
    const settingsEvents = wrapper.emitted()['update:settings']
    expect(settingsEvents).toBeTruthy()
    expect(settingsEvents![settingsEvents.length - 1][0].adminUsername).toBe('newadmin')

    // PasswordInput emits update:modelValue -> should cause update:settings with adminPassword
    const pw = wrapper.findComponent(PasswordInput)
    await (pw.vm as ComponentPublicInstance).$emit('update:modelValue', 's3cret')

    const settingsEvents2 = wrapper.emitted()['update:settings']
    expect(settingsEvents2).toBeTruthy()
    expect(settingsEvents2![settingsEvents2.length - 1][0].adminPassword).toBe('s3cret')
  })

  it('emits update:apiKey when ApiKeyControl emits update:apiKey', async () => {
    const { default: AuthenticationSection } =
      await import('@/components/settings/AuthenticationSection.vue')
    const { default: ApiKeyControl } = await import('@/components/ui/ApiKeyControl.vue')

    const wrapper = mount(AuthenticationSection, {
      props: {
        settings: { adminUsername: 'admin', adminPassword: '' },
        authEnabled: true,
        apiKey: 'OLD',
      },
      global: { components: { ApiKeyControl } },
    })

    const api = wrapper.findComponent(ApiKeyControl)
    await (api.vm as ComponentPublicInstance).$emit('update:apiKey', 'NEW')

    expect(wrapper.emitted()['update:apiKey']).toBeTruthy()
    expect(wrapper.emitted()['update:apiKey']![0]).toEqual(['NEW'])
  })
})
