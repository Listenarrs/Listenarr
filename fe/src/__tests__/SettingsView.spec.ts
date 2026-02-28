/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, it, expect, vi, beforeEach } from 'vitest'
import type { Mock } from 'vitest'
import { mount } from '@vue/test-utils' 
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import SettingsView from '@/views/SettingsView.vue'
import { apiService } from '@/services/api'

vi.mock('@/services/api', () => ({
  apiService: {
    getStartupConfig: vi.fn(),
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
  type DownloadClient = {
    id: string
    name: string
    type: string
    host: string
    port: number
    isEnabled: boolean
    useSSL: boolean
    downloadPath: string
  }
  beforeEach(() => {
    ;(apiService.getStartupConfig as Mock).mockReset()
    // Provide a single Pinia instance for stores used by the component
    const pinia = createPinia()
    setActivePinia(pinia)
  })

  it('sets authEnabled when startup config AuthenticationRequired is Enabled', async () => {
    ;(apiService.getStartupConfig as Mock).mockResolvedValue({ AuthenticationRequired: 'Enabled' })
    // create a minimal router for components that inject router/location
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    // Ensure router is ready before mounting (SettingsView may call router.replace during mount)
    await router.push('/')
    await router.isReady().catch(() => {})
    const pinia = createPinia()
    const wrapper = mount(SettingsView, {
      global: { plugins: [pinia, router], stubs: ['FolderBrowser'] },
    })
    // Wait for onMounted async calls to finish
    await new Promise((r) => setTimeout(r, 0))
    // Accept both legacy 'Enabled' and new 'true' string values
    const vm = wrapper.vm as unknown as { authEnabled?: boolean }
    expect(vm.authEnabled).toBe(true)
  })

  it('toggles password visibility', async () => {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})
    const pinia = createPinia()
    const wrapper = mount(SettingsView, {
      global: { plugins: [pinia, router], stubs: ['FolderBrowser'] },
    })
    // Activate the General Settings tab so the password field is rendered
    const generalTab = wrapper
      .findAll('button.tab-button')
      .find((b) => b.text().includes('General Settings'))
    expect(generalTab).toBeTruthy()
    await generalTab!.trigger('click')
    // Provide settings so the admin password input is rendered
    const vm = wrapper.vm as unknown as {
      settings?: Settings
      $?: { setupState?: SetupState }
      $setup?: SetupState
      toggleShowPassword?: () => void
    }
    vm.settings = { adminPassword: 'secret' }
    await wrapper.vm.$nextTick()
    await new Promise((r) => setTimeout(r, 0))
    // Access internal setup state to check showPassword directly (more reliable in VTU)
    const setupState = vm.$?.setupState ?? vm.$setup ?? (vm as unknown as SetupState)
    // initial value should be false
    expect((setupState.showPassword as any)?.value ?? (setupState.showPassword as any)).toBe(false)
    // Toggle via exposed function
    vm.toggleShowPassword?.()
    await wrapper.vm.$nextTick()
    expect((setupState.showPassword as any)?.value ?? (setupState.showPassword as any)).toBe(true)
  })

  // Note: legacy "Prefer US domain" setting was removed from the UI;
  // related tests removed to reflect current application state.

  it('applies child updates (via events) to settings and includes them when saving', async () => {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    const pinia = createPinia()
    setActivePinia(pinia)

    const wrapper = mount(SettingsView, {
      global: { plugins: [pinia, router], stubs: ['FolderBrowser'] },
    })

    // Activate General Settings tab and provide initial settings
    const generalTab = wrapper
      .findAll('button.tab-button')
      .find((b) => b.text().includes('General Settings'))
    expect(generalTab).toBeTruthy()
    await generalTab!.trigger('click')

    const vm = wrapper.vm as unknown as { settings?: Settings }
    vm.settings = {
      folderNamingPattern: '{Author}/{Series}/{Title}',
      fileNamingPattern: '{Title}',
    } as unknown as Settings

    await wrapper.vm.$nextTick()
    await new Promise((r) => setTimeout(r, 0))

    // Find the File Naming Pattern input inside the child and change it
    const fileNamingInput = wrapper.find('input[placeholder="{Title}"]')
    expect(fileNamingInput.exists()).toBe(true)
    await fileNamingInput.setValue('{Title}-{DiskNumber}')
    await new Promise((r) => setTimeout(r, 0))

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
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    const pinia = createPinia()
    setActivePinia(pinia)

    // Prepare configuration store with a single disabled client
    const { useConfigurationStore } = await import('@/stores/configuration')
    const cfgStore = useConfigurationStore()
    cfgStore.downloadClientConfigurations = [] as any
    cfgStore.downloadClientConfigurations.push({
      id: 'client-1',
      name: 'Test Client',
      type: 'qbittorrent',
      host: 'localhost',
      port: 8080,
      isEnabled: false,
      useSSL: false,
      downloadPath: '',
    } as any)

    // Prevent load from overwriting our test data
    cfgStore.loadDownloadClientConfigurations = vi.fn(async () => {})

    cfgStore.saveDownloadClientConfiguration = vi.fn(async (c: Partial<DownloadClient>) => {
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
    await wrapper.vm.$nextTick()

    // Call the toggle handler directly (avoid relying on rendered DOM in VTU)
    const vm2 = wrapper.vm as unknown as {
      toggleDownloadClientFunc?: (c: DownloadClient) => Promise<void>
    }
    await vm2.toggleDownloadClientFunc?.(cfgStore.downloadClientConfigurations[0])
    // Wait for async save
    await new Promise((r) => setTimeout(r, 0))

    expect(cfgStore.saveDownloadClientConfiguration).toHaveBeenCalled()
    expect(cfgStore.downloadClientConfigurations[0].isEnabled).toBe(true)
  })

  it('renders Root Folders in its own tab', async () => {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: { template: '<div />' } }],
    })
    await router.push('/')
    await router.isReady().catch(() => {})

    const pinia = createPinia()
    setActivePinia(pinia)

    const wrapper = mount(SettingsView, {
      global: { plugins: [pinia, router], stubs: ['FolderBrowser'] },
    })

    const tab = wrapper.findAll('button.tab-button').find((b) => b.text().includes('Root Folders'))
    expect(tab).toBeTruthy()
    await tab!.trigger('click')
    // Wait for router navigation to complete and nextTick
    await router.isReady().catch(() => {})
    await new Promise((r) => setTimeout(r, 0))
    await wrapper.vm.$nextTick()

    // Ensure the Root Folders tab became active
    expect(tab!.classes()).toContain('active')
  })
})
