import { mount } from '@vue/test-utils'
import { vi, describe, it, expect, beforeEach } from 'vitest'

vi.mock('@/services/api', () => ({
  apiService: {
    getQualityProfiles: vi.fn().mockResolvedValue([]),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
    updateAudiobook: vi.fn().mockResolvedValue({ message: 'ok', audiobook: {} }),
    moveAudiobook: vi.fn().mockResolvedValue({ message: 'queued', jobId: 'job-1' }),
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ info: vi.fn(), success: vi.fn(), error: vi.fn() }),
}))

vi.mock('@/services/signalr', () => ({
  signalRService: {
    onMoveJobUpdate: vi.fn(() => () => {}),
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

describe('EditAudiobookModal move options', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('Change without moving should update audiobook and not call move API', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    // let init settle
    await new Promise((r) => setTimeout(r, 200))

    // Ensure there is a detectable change: set an explicit custom root and flip monitored
    ;(wrapper.vm as any).selectedRootId = 0
    ;(wrapper.vm as any).customRootPath = 'C:\\root\\New Author\\New Book'
    ;(wrapper.vm as any).formData.monitored = false
    await wrapper.vm.$nextTick()

    // Start save flow and resolve the in-component confirmation promise by
    // calling the module-scoped resolver if it was created. This avoids
    // relying on modal rendering in jsdom.
    const savePromise = (wrapper.vm as any).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver = (wrapper.vm as any).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: false, deleteEmptySource: false })
    await savePromise
    // Allow async work to settle
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(0)
  })

  it('Move should call move API with deleteEmptySource true by default', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))

    // Ensure there is a detectable change: set an explicit custom root and flip monitored
    ;(wrapper.vm as any).selectedRootId = 0
    ;(wrapper.vm as any).customRootPath = 'C:\\root\\New Author\\New Book'
    ;(wrapper.vm as any).formData.monitored = false
    await wrapper.vm.$nextTick()

    // Start save flow and resolve the in-component confirmation promise to
    // simulate the user choosing to move files now.
    const savePromise2 = (wrapper.vm as any).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver2 = (wrapper.vm as any).moveConfirmResolver
    if (resolver2) resolver2({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise2

    // Wait for async update + move to settle
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.moveAudiobook).toHaveBeenCalledWith(
      expect.anything(),
      expect.anything(),
      expect.objectContaining({ moveFiles: true, deleteEmptySource: true }),
    )
  })
})
