import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import DownloadClientsTab from '@/views/settings/DownloadClientsTab.vue'
import { useConfigurationStore } from '@/stores/configuration'

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
    const client: any = {
      id: 'db-1',
      name: 'qbt',
      type: 'qbittorrent',
      host: 'dbhost.local',
      port: 8080,
      isEnabled: true,
      useSSL: false,
      downloadPath: '',
      username: '',
      password: '',
      settings: {},
    }
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