import { mount } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import { describe, it, beforeEach, expect, vi } from 'vitest'
import { signalRService } from '@/services/signalr'
import AudiobookDetailView from '@/views/library/AudiobookDetailView.vue'
import { useLibraryStore } from '@/stores/library'
import type { Audiobook } from '@/types'

// Mock useRoute to provide params for the detail view
vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: '1' } }),
  useRouter: () => ({ push: vi.fn() }),
}))

describe('AudiobookDetailView SignalR integration', () => {
  let pinia: ReturnType<typeof createPinia>

  beforeEach(() => {
    pinia = createPinia()
    setActivePinia(pinia)
    vi.clearAllMocks()
  })

  it('updates displayed base path and files when an AudiobookUpdate arrives', async () => {
    // Capture registered callbacks
    const callbacks: Array<(a: Audiobook) => void> = []
    const spy = vi.spyOn(signalRService, 'onAudiobookUpdate').mockImplementation((cb?: (...args: unknown[]) => void) => {
      if (cb) callbacks.push(cb as (a: Audiobook) => void)
      return () => {}
    })

    // Ensure other signalR callbacks used by the component exist to avoid runtime errors
    ;(signalRService as any).onScanJobUpdate = (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    }

    // Seed the library store with an initial audiobook
    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Initial Title',
        basePath: '/old/path',
        files: [{ id: 10, path: '/old/path/oldfile.m4b', format: 'm4b', durationSeconds: 100 }],
      },
    ] as Audiobook[]

    // Mount the component
    const wrapper = mount(AudiobookDetailView, {
      global: { plugins: [pinia] },
    })

    // Allow loadAudiobook to finish
    await new Promise((r) => setTimeout(r, 10))
    await wrapper.vm.$nextTick()

    // Assert initial values present in DOM (details tab)
    expect(wrapper.find('.file-path').text()).toContain('/old/path')

    // Switch to Files tab to check file list
    const fileTab = wrapper.findAll('.tab').find((t) => t.text().includes('Files'))
    expect(fileTab).toBeDefined()
    await fileTab!.trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.findAll('.file-name').some((n) => n.text().includes('oldfile.m4b'))).toBe(true)

    // Simulate server pushing an update with new basePath and files
    const serverDto: Partial<Audiobook> = {
      id: 1,
      title: 'Updated Title',
      basePath: '/new/path',
      files: [{ id: 11, path: '/new/path/newfile.m4b', format: 'm4b', durationSeconds: 200 }],
    }

    // Ensure callback was registered and invoke it
    expect(callbacks.length).toBeGreaterThan(0)

    // Ensure other signalR callbacks used by the component exist to avoid runtime errors
    ;(signalRService as any).onScanJobUpdate = (cb?: (...args: unknown[]) => void) => {
      void cb
      return () => {}
    }

    callbacks[0](serverDto as Audiobook)

    // Wait for merge and DOM update
    await new Promise((r) => setTimeout(r, 10))
    await wrapper.vm.$nextTick()

    // Assert DOM updated
    expect(wrapper.find('.file-path').text()).toContain('/new/path')
    const fileNames = wrapper.findAll('.file-name').map((n) => n.text())
    expect(fileNames.some((n) => n.includes('newfile.m4b'))).toBe(true)
    expect(fileNames.some((n) => n.includes('oldfile.m4b'))).toBe(false)

    spy.mockRestore()
  })
})
