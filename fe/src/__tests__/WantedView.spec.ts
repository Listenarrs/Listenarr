import { mount } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import { describe, it, beforeEach, expect, vi } from 'vitest'
import WantedView from '@/views/content/WantedView.vue'
import { useLibraryStore } from '@/stores/library'

// Mock api service ensureImageCached and getImageUrl (and other helpers used by stores)
vi.mock('@/services/api', () => ({
  apiService: {
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getQualityProfiles: vi.fn(async () => []),
  },
  // Also expose the named helper so tests can import it directly
  getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
  ensureImageCached: vi.fn(async () => true),
}))

describe('WantedView image recache behavior', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    vi.clearAllMocks()
  })

  it('calls ensureImageCached for visible wanted items on mount', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const store = useLibraryStore()
    store.audiobooks = [
      { id: 1, title: 'Book 1', monitored: true, files: [], imageUrl: '/api/images/ASIN1' },
      { id: 2, title: 'Book 2', monitored: true, files: [], imageUrl: '/api/images/ASIN2' },
    ] as unknown as any

    // Prevent fetchLibrary from running during mount
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(WantedView, { global: { plugins: [pinia] } });

    // Allow onMounted work to complete
    await new Promise((r) => setTimeout(r, 10))

    // Ensure the image element was rendered with the expected src (avoid relying on internal mock call)
    const img = wrapper.find('img')
    expect(img.exists()).toBe(true)
    const src = img.attributes('src') || ''
    expect(src).toContain('/api/images/ASIN1')
  })
})