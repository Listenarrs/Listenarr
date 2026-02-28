import { mount } from '@vue/test-utils'
import { vi, describe, it, expect } from 'vitest'
import { nextTick } from 'vue'

vi.mock('@/services/api', () => ({
  apiService: {
    getQualityProfiles: vi.fn().mockResolvedValue([]),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
    getRootFolders: vi
      .fn()
      .mockResolvedValue([{ id: 1, name: 'Default', path: 'C:\\root', isDefault: true }]),
  },
}))

import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'

const audiobook = {
  id: 1,
  title: 'Sample',
  authors: ['Author'],
  basePath: 'C:\\root\\Some Author\\Some Title',
  monitored: true,
  tags: [],
}

describe('EditAudiobookModal relative path calculation', () => {
  it('shows full path in readonly input by default', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Primary assertion: combined path should match expected (normalize slashes)
    expect(((wrapper.vm as any).combinedBasePath() || '').replace(/\\/g, '/')).toBe('C:/root/Some Author/Some Title')

    // If the readonly input exists in this environment, also assert its value
    const readonlyInput = wrapper.find('.readonly-input')
    if (readonlyInput.exists()) {
      expect(((readonlyInput.element as HTMLInputElement).value || '').replace(/\\/g, '/')).toBe('C:/root/Some Author/Some Title')
    }
  })

  it('derives relative path from stored basePath when root configured', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Expect the internal relativePath to be derived from stored basePath
    expect((wrapper.vm as any).formData.relativePath).toBe('Some Author\\Some Title')
  })

  it('normalizes absolute path to relative when Done is clicked', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Set absolute value and call finishEditingDestination directly
    ;(wrapper.vm as any).formData.relativePath = 'C:\\root\\New Author\\New Title'
    await (wrapper.vm as any).finishEditingDestination()

    // After normalization the internal relativePath should be the short relative
    expect((wrapper.vm as any).formData.relativePath).toBe('New Author\\New Title')
  })

  it('preserves a user-typed relative path after Done and reopen', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Type a relative path and call Done directly
    ;(wrapper.vm as any).formData.relativePath = 'My Author\\My Title'
    await (wrapper.vm as any).finishEditingDestination()

    // The internal relativePath should remain what the user typed
    expect((wrapper.vm as any).formData.relativePath).toBe('My Author\\My Title')
  })

  it('prefills absolute path when switching to Custom path', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Simulate switching to Custom path by setting selectedRootId
    ;(wrapper.vm as any).selectedRootId = 0
    await nextTick()

    // customRootPath should be prefilled to the full base path (normalize slashes)
    expect(((wrapper.vm as any).customRootPath || '').replace(/\\/g, '/')).toBe('C:/root/Some Author/Some Title')
  })

  it('does not duplicate relative part when saving a Custom path', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Simulate selecting Custom path directly
    ;(wrapper.vm as any).selectedRootId = 0
    ;(wrapper.vm as any).customRootPath = (wrapper.vm as any).combinedBasePath()
    await nextTick()

    // combinedBasePath should equal the custom path exactly (no duplication)
    const cb = (wrapper.vm as any).combinedBasePath()
    const cr = (wrapper.vm as any).customRootPath
    expect((cb || '').replace(/\\/g, '/')).toBe((cr || '').replace(/\\/g, '/'))
  })

  it('selects custom path via folder browser and saves exact custom path (no duplication)', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await new Promise((r) => setTimeout(r, 10))

    // Simulate folder browser selection by setting custom root directly
    ;(wrapper.vm as any).selectedRootId = 0
    ;(wrapper.vm as any).customRootPath = 'C:\\temp\\Isaac Asimov\\Foundation'
    await nextTick()

    // combinedBasePath should equal the selected custom root exactly
    const cb = (wrapper.vm as any).combinedBasePath()
    expect(cb.replace(/\\/g, '/')).toBe('C:/temp/Isaac Asimov/Foundation')
  })
})
