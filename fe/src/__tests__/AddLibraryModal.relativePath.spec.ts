import { mount } from '@vue/test-utils'
import { vi, describe, it, expect } from 'vitest'

// Mock apiService methods used during mount/seedPreview to avoid network calls
vi.mock('@/services/api', () => ({
  apiService: {
    getMetadata: vi.fn().mockResolvedValue(null),
    previewLibraryPath: vi
      .fn()
      .mockResolvedValue({ fullPath: 'C:\\root\\Author\\Title', relativePath: '' }),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
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

describe('AddLibraryModal relative path derivation', () => {
  it('shows relative path (full minus root) when preview returns fullPath and root configured', async () => {
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
    // allow watchers / async ops
    await new Promise((r) => setTimeout(r, 10))

    const input = wrapper.find('input.relative-input')
    expect(input.exists()).toBe(true)
    expect((input.element as HTMLInputElement).value).toBe('Author\\Title')
  })
})
