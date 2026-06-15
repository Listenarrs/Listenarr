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
import type { Mock } from 'vitest'
import { mount } from '@vue/test-utils'
import SettingsView from '@/views/SettingsView.vue'
import { apiService } from '@/services/api'
import { createDownloadClientConfiguration } from '@/test/factories/downloadClient'
import { createTestPinia, createTestRouter, mountWithPiniaAndRouter } from '@/test/utils/mount'
import { flushAsync } from '@/test/utils/wait'

const mockAuthStore = vi.hoisted(() => ({
  user: { authenticated: true },
  loadCurrentUser: vi.fn(async () => undefined),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getBootstrapConfig: vi.fn(async () => ({ AuthenticationRequired: 'Enabled' })),
    getStartupConfig: vi.fn(),
    getApiKey: vi.fn(async () => ({ apiKey: 'abc' })),
    getApiConfigurations: vi.fn(async () => []),
    getDownloadClientConfigurations: vi.fn(async () => []),
    getApplicationSettings: vi.fn(async () => ({})),
    getIndexers: vi.fn(async () => []),
    getQualityProfiles: vi.fn(async () => []),
    getAdminUsers: vi.fn(async () => []),
    generateInitialApiKey: vi.fn(async () => ({ apiKey: 'abc' })),
    regenerateApiKey: vi.fn(async () => ({ apiKey: 'abc' })),
    saveStartupConfig: vi.fn(async () => ({})),
  },
  // Named exports used directly by SettingsView.vue
  getIndexers: vi.fn(async () => []),
  deleteIndexer: vi.fn(async () => ({})),
  toggleIndexer: vi.fn(async (id: number) => ({ id, isEnabled: true })),
  testIndexer: vi.fn(async (id: number) => ({
    success: true,
    message: 'ok',
    indexer: { id, isEnabled: true },
  })),
  getQualityProfiles: vi.fn(async () => []),
  deleteQualityProfile: vi.fn(async () => ({})),
  createQualityProfile: vi.fn(async (p: unknown) => p),
  updateQualityProfile: vi.fn(async (id: number, p: unknown) => p),
  getRemotePathMappings: vi.fn(async () => []),
  createRemotePathMapping: vi.fn(async (p: unknown) => p),
  updateRemotePathMapping: vi.fn(async (id: number, p: unknown) => p),
  deleteRemotePathMapping: vi.fn(async () => ({})),
}))

vi.mock('@/stores/auth', () => ({
  useAuthStore: () => mockAuthStore,
}))

describe('SettingsView', () => {
  type SetupState = { showPassword?: { value: boolean } | boolean }
  type Settings = {
    adminPassword?: string
    useUsProxy?: boolean
    usProxyHost?: string
    usProxyPort?: number
    usProxyUsername?: string
    usProxyPassword?: string
  }
  beforeEach(() => {
    ;(apiService.getStartupConfig as Mock).mockReset()
    mockAuthStore.user.authenticated = true
    mockAuthStore.loadCurrentUser.mockClear()
    // Provide a single Pinia instance for stores used by the component
    createTestPinia()
  })

  it('sets authEnabled when startup config AuthenticationRequired is Enabled', async () => {
    ;(apiService.getStartupConfig as Mock).mockResolvedValue({ AuthenticationRequired: 'Enabled' })
    const wrapper = await mountWithPiniaAndRouter(SettingsView, {
      global: { stubs: ['FolderBrowser'] },
    })
    // Wait for onMounted async calls to finish
    await flushAsync()
    // Accept both legacy 'Enabled' and new 'true' string values
    const vm = wrapper.vm as any as { authEnabled?: boolean }
    expect(vm.authEnabled).toBe(true)
  })

  it('toggles password visibility', async () => {
    const wrapper = await mountWithPiniaAndRouter(SettingsView, {
      global: { stubs: ['FolderBrowser'] },
    })
    // Activate the General Settings tab so the password field is rendered
    const generalTab = wrapper
      .findAll('button.tab-button')
      .find((b) => b.text().includes('General Settings'))
    expect(generalTab).toBeTruthy()
    await generalTab!.trigger('click')
    // Provide settings so the admin password input is rendered
    const vm = wrapper.vm as any as {
      settings?: Settings
      $?: { setupState?: SetupState }
      $setup?: SetupState
      toggleShowPassword?: () => void
    }
    vm.settings = { adminPassword: 'secret' }
    await flushAsync()
    // Access internal setup state to check showPassword directly (more reliable in VTU)
    const setupState = vm.$?.setupState ?? vm.$setup ?? (vm as any as SetupState)
    // initial value should be false
    expect((setupState.showPassword as any)?.value ?? (setupState.showPassword as any)).toBe(false)
    // Toggle via exposed function
    vm.toggleShowPassword?.()
    await flushAsync()
    expect((setupState.showPassword as any)?.value ?? (setupState.showPassword as any)).toBe(true)
  })

  // Note: legacy "Prefer US domain" setting was removed from the UI;
  // related tests removed to reflect current application state.

  it('applies child updates (via events) to settings and includes them when saving', async () => {
    const wrapper = await mountWithPiniaAndRouter(SettingsView, {
      global: { stubs: ['FolderBrowser'] },
    })

    // Activate General Settings tab and provide initial settings
    const generalTab = wrapper
      .findAll('button.tab-button')
      .find((b) => b.text().includes('General Settings'))
    expect(generalTab).toBeTruthy()
    await generalTab!.trigger('click')

    const vm = wrapper.vm as any as { settings?: Settings }
    vm.settings = {
      folderNamingPattern: '{Author}/{Series}/{Title}',
      fileNamingPattern: '{Title}',
    } as any as Settings

    await flushAsync()

    // Find the File Naming Pattern input inside the child and change it
    const fileNamingInput = wrapper.find('input[placeholder="{Title}"]')
    expect(fileNamingInput.exists()).toBe(true)
    await fileNamingInput.setValue('{Title}-{DiskNumber}')
    await flushAsync()

    // Spy on the configuration store save method
    const { useConfigurationStore } = await import('@/stores/configuration')
    const cfgStore = useConfigurationStore()
    cfgStore.saveApplicationSettings = vi.fn().mockResolvedValue(undefined)

    // Save settings and assert that the updated value from the child is included
    const saveBtn = wrapper
      .findAll('button.btn.btn-primary')
      .find((b) => b.text().includes('Save Settings'))
    expect(saveBtn).toBeTruthy()
    await saveBtn!.trigger('click')

    expect(cfgStore.saveApplicationSettings).toHaveBeenCalled()
    const calledWith = (cfgStore.saveApplicationSettings as Mock).mock.calls[0][0]
    expect(calledWith.fileNamingPattern).toBe('{Title}-{DiskNumber}')
  })

  it('toggles download client enabled state', async () => {
    const pinia = createTestPinia()
    const { router, ready } = createTestRouter()
    await ready()

    // Prepare configuration store with a single disabled client
    const { useConfigurationStore } = await import('@/stores/configuration')
    const cfgStore = useConfigurationStore()
    cfgStore.downloadClientConfigurations = []
    cfgStore.downloadClientConfigurations.push(
      createDownloadClientConfiguration({
        id: 'client-1',
        name: 'Test Client',
        host: 'localhost',
        port: 8080,
        isEnabled: false,
      }),
    )

    // Prevent load from overwriting our test data
    cfgStore.loadDownloadClientConfigurations = vi.fn(async () => {})

    cfgStore.saveDownloadClientConfiguration = vi.fn(async (c) => {
      // Simulate backend saving (no-op)
      cfgStore.downloadClientConfigurations[0] = c as any
      return Promise.resolve()
    })

    const wrapper = mount(SettingsView, {
      global: { plugins: [pinia, router], stubs: ['FolderBrowser'] },
    })

    // Activate the clients tab
    const clientsTab = wrapper
      .findAll('button.tab-button')
      .find((b) => b.text().includes('Download Clients'))
    expect(clientsTab).toBeTruthy()
    await clientsTab!.trigger('click')
    await flushAsync()

    // Call the toggle handler directly (avoid relying on rendered DOM in VTU)
    const vm2 = wrapper.vm as any as {
      toggleDownloadClientFunc?: (
        c: ReturnType<typeof createDownloadClientConfiguration>,
      ) => Promise<void>
    }
    await vm2.toggleDownloadClientFunc?.(cfgStore.downloadClientConfigurations[0])
    // Wait for async save
    await flushAsync()

    expect(cfgStore.saveDownloadClientConfiguration).toHaveBeenCalled()
    expect(cfgStore.downloadClientConfigurations[0].isEnabled).toBe(true)
  })

  it('renders Root Folders in its own tab', async () => {
    const wrapper = await mountWithPiniaAndRouter(SettingsView, {
      global: { stubs: ['FolderBrowser'] },
    })

    const tab = wrapper.findAll('button.tab-button').find((b) => b.text().includes('Root Folders'))
    expect(tab).toBeTruthy()
    await tab!.trigger('click')
    await flushAsync()

    // Ensure the Root Folders tab became active
    expect(tab!.classes()).toContain('active')
  })
})
