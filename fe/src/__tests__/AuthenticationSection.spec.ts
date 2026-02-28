import { describe, it, expect, vi, beforeEach } from 'vitest'
import type { ComponentPublicInstance } from 'vue'
import { mount } from '@vue/test-utils'

import PasswordInput from '@/components/form/PasswordInput.vue'
import Checkbox from '@/components/form/Checkbox.vue'

describe('AuthenticationSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('emits update:authEnabled when checkbox toggled', async () => {
    const { default: AuthenticationSection } = await import('@/components/settings/AuthenticationSection.vue')
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
    const { default: AuthenticationSection } = await import('@/components/settings/AuthenticationSection.vue')
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

  it('emits update:startupConfig when ApiKeyControl emits update:apiKey', async () => {
    const { default: AuthenticationSection } = await import('@/components/settings/AuthenticationSection.vue')
    const { default: ApiKeyControl } = await import('@/components/ui/ApiKeyControl.vue')

    const wrapper = mount(AuthenticationSection, {
      props: { settings: { adminUsername: 'admin', adminPassword: '' }, authEnabled: true, startupConfig: { apiKey: 'OLD' } },
      global: { components: { ApiKeyControl } },
    })

    const api = wrapper.findComponent(ApiKeyControl)
    await (api.vm as ComponentPublicInstance).$emit('update:apiKey', 'NEW')

    expect(wrapper.emitted()['update:startupConfig']).toBeTruthy()
    expect(wrapper.emitted()['update:startupConfig']![0][0].apiKey).toBe('NEW')
  })
})
