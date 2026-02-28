import { mount } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import { describe, it, beforeEach, expect, vi } from 'vitest'
// Mock useRoute to provide params for the detail view
vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: '5' } }),
  useRouter: () => ({ push: vi.fn() }),
}))

// Mock api service ensureImageCached and getImageUrl
vi.mock('@/services/api', () => ({
  apiService: {
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getQualityProfiles: vi.fn(async () => []),
    getLibrary: vi.fn(async () => []),
  },
  ensureImageCached: vi.fn(async () => true),
}))

// Mock signalr service to provide missing hooks (e.g., onScanJobUpdate)
vi.mock('@/services/signalr', () => ({
  signalRService: {
    connect: vi.fn(async () => undefined),
    onQueueUpdate: vi.fn(() => () => undefined),
    onFilesRemoved: vi.fn(() => () => undefined),
    onToast: vi.fn(() => () => undefined),
    onAudiobookUpdate: vi.fn(() => () => undefined),
    onDownloadUpdate: vi.fn(() => () => undefined),
    onDownloadsList: vi.fn(() => () => undefined),
    onScanJobUpdate: vi.fn(() => () => undefined),
  },
}))

import AudiobookDetailView from '@/views/library/AudiobookDetailView.vue'
import { useLibraryStore } from '@/stores/library'

describe('AudiobookDetailView image recache behavior', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    vi.clearAllMocks()
  })

  it('calls ensureImageCached for the audiobook cover on load', async () => {
    // Ensure a fresh module cache so mocks take effect for this test
    vi.resetModules()

    // Create a fresh Pinia instance and register it as active so both the test and component share it
    const pinia = createPinia()
    setActivePinia(pinia)

    const { useLibraryStore } = await import('@/stores/library')
    const store = useLibraryStore()
    store.audiobooks = [
      { id: 5, title: 'Detail Book', imageUrl: '/api/images/ASIN000005', files: [] },
    ] as unknown as any

    // Prevent actual fetchLibrary from running
    store.fetchLibrary = vi.fn(async () => undefined)

    // Re-import the component after resetting modules so it picks up the module mocks
    const { default: AudiobookDetailViewCmp } = await import('@/views/library/AudiobookDetailView.vue')
    const wrapper = mount(AudiobookDetailViewCmp, { global: { plugins: [pinia] } })

    await new Promise((r) => setTimeout(r, 10))

    const { ensureImageCached } = await import('@/services/api')
    expect(ensureImageCached).toHaveBeenCalled()
    expect((ensureImageCached as unknown as any).mock.calls[0][0]).toBe('/api/images/ASIN000005')
  })
})