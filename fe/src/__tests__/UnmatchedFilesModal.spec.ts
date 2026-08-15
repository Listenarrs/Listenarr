/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { describe, expect, it, vi } from 'vitest'
import UnmatchedFilesModal from '@/components/feedback/UnmatchedFilesModal.vue'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import { apiService } from '@/services/api'
import type { RootFolder } from '@/types'

vi.mock('@/services/api', () => ({
  apiService: {
    getSavedUnmatchedFiles: vi.fn().mockResolvedValue({
      items: [
        {
          fullPath: 'C:\\library\\Book\\01.m4b',
          bookFolder: 'C:\\library\\Book',
          relativePath: 'Book',
          title: 'Book',
          author: 'Author',
          asin: 'B000000001',
          fileCount: 1,
          format: 'M4B',
        },
      ],
      lastScannedAt: null,
    }),
    getApplicationSettings: vi.fn().mockResolvedValue({ completedFileAction: 'copy' }),
    getRootFolders: vi.fn().mockResolvedValue([]),
    scanUnmatchedFiles: vi.fn(),
    getUnmatchedResults: vi.fn(),
    getAudibleMetadata: vi.fn(),
    addToLibrary: vi.fn(),
    startManualImport: vi.fn(),
  },
}))

vi.mock('@/services/signalr', () => ({
  signalRService: {
    onUnmatchedScanComplete: vi.fn(() => () => undefined),
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({
    success: vi.fn(),
    warning: vi.fn(),
    info: vi.fn(),
  }),
}))

describe('UnmatchedFilesModal filesystem readiness', () => {
  it('keeps cached results visible but disables scan and import actions while initializing', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    useFilesystemReadinessStore().readiness = {
      isReady: true,
      status: 'ready',
      databaseConnected: true,
      migrationsCurrent: true,
      errorCode: null,
      filesystemReady: false,
      filesystemStatus: 'Running',
      filesystemPhase: 'AudiobookFileIdentities',
      filesystemErrorCode: null,
      filesystemErrorMessage: null,
    }
    const rootFolder = {
      id: 7,
      name: 'Library',
      path: 'C:\\library',
      isDefault: true,
      storageState: 'Initializing',
      canMutateFilesystem: false,
    } as unknown as RootFolder

    const wrapper = mount(UnmatchedFilesModal, {
      props: { isOpen: false, rootFolder },
      attachTo: document.body,
      global: {
        plugins: [pinia],
        stubs: {
          AddLibraryModal: true,
        },
      },
    })
    await wrapper.setProps({ isOpen: true })
    await flushPromises()

    expect(document.body.textContent).toContain('Book')
    const buttons = Array.from(document.body.querySelectorAll('button'))
    const add = buttons.find((button) => button.textContent?.trim() === 'Add')
    const addAll = buttons.find((button) => button.textContent?.includes('Add All'))
    const scan = buttons.find((button) => button.textContent?.trim() === 'Scan')
    expect(add).toBeTruthy()
    expect(addAll).toBeTruthy()
    expect(scan).toBeTruthy()
    expect(add!.disabled).toBe(true)
    expect(addAll!.disabled).toBe(true)
    expect(scan!.disabled).toBe(true)

    scan!.click()
    expect(apiService.scanUnmatchedFiles).not.toHaveBeenCalled()
    expect(document.body.querySelector('add-library-modal-stub')).toBeNull()
    wrapper.unmount()
  })
})
