import { mount } from '@vue/test-utils'
import { vi, describe, it, expect } from 'vitest'

// Mock apiService methods used during mount/seedPreview to avoid network calls
vi.mock('@/services/api', () => ({
  apiService: {
    getMetadata: vi.fn().mockResolvedValue(null),
    previewLibraryPath: vi.fn().mockResolvedValue({ fullPath: '', relativePath: '' }),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: '' }),
    getQualityProfiles: vi.fn().mockResolvedValue([]),
  },
}))

import AddLibraryModal from '@/components/domain/audiobook/AddLibraryModal.vue'

const fakeBook = {
  title: 'Test Title',
  authors: ['Author One'],
  imageUrl: '',
  asin: 'B001234567',
}

describe('AddLibraryModal accessibility', () => {
  it('renders dialog with proper ARIA and emits close on Escape', async () => {
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    // allow watchers to run
    await new Promise((r) => setTimeout(r, 0))

    // dialog exists
    const dialog = wrapper.find('[role="dialog"]')
    expect(dialog.exists()).toBe(true)
    expect(dialog.attributes('aria-modal')).toBe('true')
    expect(dialog.attributes('aria-labelledby')).toBeDefined()

    // Simulate Escape key press on document
    const escEvent = new KeyboardEvent('keydown', { key: 'Escape' })
    document.dispatchEvent(escEvent)

    // allow event loop
    await new Promise((r) => setTimeout(r, 0))

    const emitted = wrapper.emitted('close')
    expect(emitted).toBeTruthy()
  })
})
