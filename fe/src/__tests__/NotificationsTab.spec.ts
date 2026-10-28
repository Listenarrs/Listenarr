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
import { useConfigurationStore } from '@/stores/configuration'

describe('NotificationsTab', () => {
  it('shows loading state and header spinner while application settings are loading', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const cfg = useConfigurationStore()
    cfg.isLoading = true

    const NotificationsTab = (await import('@/views/settings/NotificationsTab.vue')).default
    const wrapper = mount(NotificationsTab, {
      props: { settings: null },
      global: { plugins: [pinia] },
    })

    await wrapper.vm.$nextTick()

    expect(wrapper.find('.loading-state').exists()).toBe(true)
    expect(wrapper.find('.section-header .small-inline-spinner').exists()).toBe(true)
  })

  it('uses the latest committed settings version across consecutive webhook saves', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const cfg = useConfigurationStore()
    cfg.isLoading = false
    const initialSettings = { version: 3, webhookUrl: '', webhooks: [] }
    cfg.applicationSettings = initialSettings as never
    const submittedVersions: number[] = []
    cfg.saveApplicationSettings = vi.fn(async (payload) => {
      submittedVersions.push(payload.version)
      const saved = { ...payload, version: payload.version + 1 }
      cfg.applicationSettings = saved
      return saved
    })

    const NotificationsTab = (await import('@/views/settings/NotificationsTab.vue')).default
    const wrapper = mount(NotificationsTab, {
      props: { settings: initialSettings as never },
      global: { plugins: [pinia] },
    })
    const vm = wrapper.vm as unknown as { openWebhookForm: () => void }

    async function addWebhook(name: string, url: string, expectedSaveCount: number) {
      vm.openWebhookForm()
      await wrapper.vm.$nextTick()
      await wrapper.find('#webhook-name').setValue(name)
      await wrapper.find('#webhook-type').setValue('NTFY')
      await wrapper.vm.$nextTick()
      await wrapper.find('#webhook-url').setValue(url)
      const trigger = wrapper.find('.webhook-triggers input[type="checkbox"]')
      expect(trigger.exists()).toBe(true)
      await trigger.setValue(true)
      await wrapper.find('.webhook-modal form').trigger('submit')
      await vi.waitFor(() => {
        expect(submittedVersions).toHaveLength(expectedSaveCount)
      })
    }

    await addWebhook('First webhook', 'https://ntfy.example/first', 1)
    await addWebhook('Second webhook', 'https://ntfy.example/second', 2)

    expect(submittedVersions).toEqual([3, 4])
    const updates = wrapper.emitted('update:settings') ?? []
    expect(updates).toHaveLength(2)
    expect((updates[1]?.[0] as { version: number }).version).toBe(5)
  })

  describe('webhook URL validation', () => {
    async function mountAndOpenForm() {
      const pinia = createPinia()
      setActivePinia(pinia)

      const cfg = useConfigurationStore()
      cfg.isLoading = false

      const NotificationsTab = (await import('@/views/settings/NotificationsTab.vue')).default
      const wrapper = mount(NotificationsTab, {
        props: { settings: { webhookUrl: '', webhooks: [] } },
        global: { plugins: [pinia] },
      })

      // Open the webhook form and select NTFY type
      const vm = wrapper.vm as unknown as { openWebhookForm: () => void }
      vm.openWebhookForm()
      await wrapper.vm.$nextTick()

      return wrapper
    }

    async function setUrlAndBlur(wrapper: ReturnType<typeof mount>, url: string) {
      // Set the service type to NTFY so the URL input is shown
      const typeSelect = wrapper.find('#webhook-type')
      await typeSelect.setValue('NTFY')
      await wrapper.vm.$nextTick()

      const urlInput = wrapper.find('#webhook-url')
      await urlInput.setValue(url)
      await urlInput.trigger('blur')
      await wrapper.vm.$nextTick()
    }

    it('accepts https:// webhook URLs', async () => {
      const wrapper = await mountAndOpenForm()
      await setUrlAndBlur(wrapper, 'https://ntfy.tld.com/topic')

      const error = wrapper.find('#webhook-url + .error-text')
      expect(error.exists()).toBe(false)
    })

    it('accepts http:// webhook URLs for LAN/self-hosted instances', async () => {
      const wrapper = await mountAndOpenForm()
      await setUrlAndBlur(wrapper, 'http://ntfy.local/topic')

      const error = wrapper.find('#webhook-url + .error-text')
      expect(error.exists()).toBe(false)
    })

    it('rejects non-HTTP(S) schemes such as ftp://', async () => {
      const wrapper = await mountAndOpenForm()
      await setUrlAndBlur(wrapper, 'ftp://ntfy.local/topic')

      const error = wrapper.find('#webhook-url + .error-text')
      expect(error.exists()).toBe(true)
      expect(error.text()).toContain('valid URL')
    })
  })
})
