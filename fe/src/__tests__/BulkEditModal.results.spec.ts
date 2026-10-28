/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import BulkEditModal from '@/components/domain/collection/BulkEditModal.vue'
import { executeBulkEdit } from '@/utils/bulkEditOrchestration'

const success = vi.fn()
const error = vi.fn()
const info = vi.fn()

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success, error, info }),
}))

vi.mock('@/utils/bulkEditOrchestration', () => ({
  executeBulkEdit: vi.fn(),
}))

const executeBulkEditMock = vi.mocked(executeBulkEdit)

describe('BulkEditModal results', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    const pinia = createPinia()
    setActivePinia(pinia)
  })

  it('keeps the modal open and does not emit saved when any item fails', async () => {
    executeBulkEditMock.mockResolvedValue({
      results: [
        { id: 1, success: true, pathChangeOutcome: 'none', errors: [] },
        {
          id: 2,
          success: false,
          metadataUpdated: false,
          pathChangeOutcome: 'failed',
          errors: ['queue unavailable'],
        },
      ],
    })
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(BulkEditModal, {
      props: {
        isOpen: true,
        selectedCount: 2,
        selectedIds: new Set([1, 2]),
      },
      global: {
        plugins: [pinia],
        stubs: {
          Modal: { template: '<div><slot /><slot name="footer" /></div>' },
          ModalBody: { template: '<div><slot /></div>' },
          ModalHeader: true,
          MoveAudiobookModal: true,
          RootFolderSelect: true,
          Checkbox: true,
        },
      },
    })
    const vm = wrapper.vm as unknown as {
      formData: { monitored: boolean | null }
      handleSave: () => Promise<void>
      showResults: boolean
      results: Array<{ id: number; success: boolean; errors: string[] }>
    }
    vm.formData.monitored = true

    await vm.handleSave()

    expect(vm.showResults).toBe(true)
    expect(vm.results).toEqual([
      { id: 1, success: true, pathChangeOutcome: 'none', errors: [] },
      {
        id: 2,
        success: false,
        metadataUpdated: false,
        pathChangeOutcome: 'failed',
        errors: ['queue unavailable'],
      },
    ])
    expect(error).toHaveBeenCalledWith(
      'Bulk update incomplete',
      expect.stringContaining('1 succeeded, 0 partially succeeded, and 1 failed'),
    )
    expect(wrapper.emitted('saved')).toBeUndefined()
    expect(wrapper.emitted('close')).toBeUndefined()
  })

  it('renders a partial result distinctly and keeps the modal open', async () => {
    executeBulkEditMock.mockResolvedValue({
      results: [
        {
          id: 1,
          success: false,
          metadataUpdated: true,
          pathChangeOutcome: 'not-enqueued',
          errors: ['queue unavailable'],
        },
      ],
    })
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(BulkEditModal, {
      props: {
        isOpen: true,
        selectedCount: 1,
        selectedIds: new Set([1]),
      },
      global: {
        plugins: [pinia],
        stubs: {
          Modal: { template: '<div><slot /><slot name="footer" /></div>' },
          ModalBody: { template: '<div><slot /></div>' },
          ModalHeader: true,
          MoveAudiobookModal: true,
          RootFolderSelect: true,
          Checkbox: true,
        },
      },
    })
    const vm = wrapper.vm as unknown as {
      formData: { monitored: boolean | null }
      handleSave: () => Promise<void>
    }
    vm.formData.monitored = true

    await vm.handleSave()

    expect(wrapper.text()).toContain('0 succeeded, 1 partially succeeded, 0 failed')
    expect(wrapper.text()).toContain('Partial')
    expect(wrapper.text()).toContain('Metadata saved; the requested move was not queued.')
    expect(error).toHaveBeenCalledWith(
      'Bulk update incomplete',
      expect.stringContaining('0 succeeded, 1 partially succeeded, and 0 failed'),
    )
    expect(wrapper.emitted('saved')).toBeUndefined()
    expect(wrapper.emitted('close')).toBeUndefined()
  })

  it('emits saved and closes only when every item succeeds', async () => {
    executeBulkEditMock.mockResolvedValue({
      results: [
        { id: 1, success: true, pathChangeOutcome: 'none', errors: [] },
        { id: 2, success: true, pathChangeOutcome: 'none', errors: [] },
      ],
    })
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(BulkEditModal, {
      props: {
        isOpen: true,
        selectedCount: 2,
        selectedIds: new Set([1, 2]),
      },
      global: {
        plugins: [pinia],
        stubs: {
          Modal: { template: '<div><slot /><slot name="footer" /></div>' },
          ModalBody: { template: '<div><slot /></div>' },
          ModalHeader: true,
          MoveAudiobookModal: true,
          RootFolderSelect: true,
          Checkbox: true,
        },
      },
    })
    const vm = wrapper.vm as unknown as {
      formData: { monitored: boolean | null }
      handleSave: () => Promise<void>
    }
    vm.formData.monitored = true

    await vm.handleSave()

    expect(success).toHaveBeenCalledWith('Bulk update', 'Updated 2 audiobook(s)')
    expect(wrapper.emitted('saved')).toHaveLength(1)
    expect(wrapper.emitted('close')).toHaveLength(1)
  })
})
