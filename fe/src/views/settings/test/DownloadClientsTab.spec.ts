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
import DownloadClientsTab from '@/views/settings/DownloadClientsTab.vue'
import { useConfigurationStore } from '@/stores/configuration'
import { createDownloadClientConfiguration } from '@/test/factories/downloadClient'

vi.mock('@/services/api', async (importOriginal) => {
  const actual = await importOriginal()
  return {
    ...(actual as any),
    testDownloadClient: vi.fn(async (config) => ({ success: true, message: 'ok', client: config })),
    getRemotePathMappings: vi.fn(async () => []),
  }
})

describe('DownloadClientsTab', () => {
  it('test button on card uses DB values', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useConfigurationStore()

    // seed store with a client
    const client = createDownloadClientConfiguration({
      id: 'db-1',
      name: 'qbt',
      host: 'dbhost.local',
    })
    store.downloadClientConfigurations = [client]

    const wrapper = mount(DownloadClientsTab, {
      global: { plugins: [pinia] },
    })

    // click the Test button on the card
    const testButton = wrapper.findAll('button[title="Test"]')[0]
    await testButton.trigger('click')

    const api = await import('@/services/api')
    expect(api.testDownloadClient).toHaveBeenCalled()
    const calledWith = (api.testDownloadClient as any).mock.calls[0][0]
    expect(calledWith.host).toBe('dbhost.local')
    expect(calledWith.port).toBe(8080)
  })

  it('shows loading state while download client configurations are loading', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useConfigurationStore()

    // simulate loading state on the store
    store.isLoading = true
    store.downloadClientConfigurations = [] as any

    const wrapper = mount(DownloadClientsTab, { global: { plugins: [pinia] } })

    expect(wrapper.find('.loading-state').exists()).toBe(true)
  })
})
