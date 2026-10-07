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
import { signalRService } from '@/services/signalr'
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
  it.each(['startup', 'poll', 'signal'] as const)(
    'ignores late %s results from a closed scan without cancelling the new scan',
    async (requestKind) => {
      vi.useFakeTimers()
      const pinia = createPinia()
      setActivePinia(pinia)
      const savedImplementation = vi
        .mocked(apiService.getSavedUnmatchedFiles)
        .getMockImplementation()!
      useFilesystemReadinessStore().readiness = {
        isReady: true,
        status: 'ready',
        databaseConnected: true,
        migrationsCurrent: true,
        errorCode: null,
        filesystemReady: true,
        filesystemStatus: 'Ready',
        filesystemPhase: null,
        filesystemErrorCode: null,
        filesystemErrorMessage: null,
      }
      vi.mocked(apiService.getSavedUnmatchedFiles).mockResolvedValue({ items: [], warnings: [] })
      vi.mocked(apiService.scanUnmatchedFiles)
        .mockResolvedValueOnce({ jobId: 'old-job' })
        .mockResolvedValueOnce({ jobId: 'new-job' })
      type Response = Awaited<ReturnType<typeof apiService.getUnmatchedResults>>
      let resolveOld!: (response: Response) => void
      const oldResponse = new Promise<Response>((resolve) => {
        resolveOld = resolve
      })
      const running: Response = { jobId: 'old-job', status: 'Processing', items: [], warnings: [] }
      const results = vi.mocked(apiService.getUnmatchedResults).mockReset()
      if (requestKind !== 'startup') results.mockResolvedValueOnce(running)
      results
        .mockReturnValueOnce(oldResponse)
        .mockResolvedValueOnce({ ...running, jobId: 'new-job' })
        .mockResolvedValue({
          jobId: 'new-job',
          status: 'Completed',
          items: [],
          warnings: ['New root warning'],
        })
      const rootFolder = { id: 7, name: 'Old root', path: 'C:\\old', isDefault: true } as RootFolder
      const wrapper = mount(UnmatchedFilesModal, {
        props: { isOpen: false, rootFolder },
        attachTo: document.body,
        global: { plugins: [pinia], stubs: { AddLibraryModal: true } },
      })
      const clickScan = () => {
        const button = Array.from(document.body.querySelectorAll('button')).find(
          (candidate) => candidate.textContent?.trim() === 'Scan',
        )
        expect(button).toBeTruthy()
        button!.click()
      }
      try {
        await wrapper.setProps({ isOpen: true })
        await flushPromises()
        clickScan()
        await flushPromises()
        if (requestKind === 'poll') await vi.advanceTimersByTimeAsync(2500)
        if (requestKind === 'signal') {
          const callback = vi.mocked(signalRService.onUnmatchedScanComplete).mock.calls.at(-1)![0]
          void callback({ jobId: 'old-job' })
        }
        await flushPromises()
        await wrapper.setProps({ isOpen: false })
        await wrapper.setProps({
          isOpen: true,
          rootFolder: { ...rootFolder, id: 8, name: 'New root' },
        })
        await flushPromises()
        clickScan()
        await flushPromises()
        resolveOld({
          jobId: 'old-job',
          status: 'Completed',
          items: [],
          warnings: ['Stale old root warning'],
        })
        await flushPromises()
        expect(document.body.textContent).not.toContain('Stale old root warning')
        expect(document.body.textContent).toContain('Scanning')
        await vi.advanceTimersByTimeAsync(2500)
        await flushPromises()
        expect(document.body.textContent).toContain('New root warning')
      } finally {
        wrapper.unmount()
        vi.useRealTimers()
        vi.clearAllMocks()
        vi.mocked(apiService.getSavedUnmatchedFiles).mockImplementation(savedImplementation)
      }
    },
  )
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

  it('shows cached partial-scan warnings even when no unmatched items were found', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    useFilesystemReadinessStore().readiness = {
      isReady: true,
      status: 'ready',
      databaseConnected: true,
      migrationsCurrent: true,
      errorCode: null,
      filesystemReady: true,
      filesystemStatus: 'Ready',
      filesystemPhase: null,
      filesystemErrorCode: null,
      filesystemErrorMessage: null,
    }
    vi.mocked(apiService.getSavedUnmatchedFiles).mockResolvedValueOnce({
      items: [],
      lastScannedAt: new Date().toISOString(),
      warnings: ['One path could not be read and was skipped.'],
    })
    const rootFolder = {
      id: 7,
      name: 'Library',
      path: 'C:\\library',
      isDefault: true,
    } as unknown as RootFolder

    const wrapper = mount(UnmatchedFilesModal, {
      props: { isOpen: false, rootFolder },
      attachTo: document.body,
      global: { plugins: [pinia], stubs: { AddLibraryModal: true } },
    })
    await wrapper.setProps({ isOpen: true })
    await flushPromises()

    expect(document.body.textContent).toContain('One path could not be read and was skipped.')
    expect(document.body.textContent).toContain('All files are in your library')
    wrapper.unmount()
  })

  it('polls a scan to completion when the SignalR terminal event is missed', async () => {
    vi.useFakeTimers()
    try {
      const pinia = createPinia()
      setActivePinia(pinia)
      useFilesystemReadinessStore().readiness = {
        isReady: true,
        status: 'ready',
        databaseConnected: true,
        migrationsCurrent: true,
        errorCode: null,
        filesystemReady: true,
        filesystemStatus: 'Ready',
        filesystemPhase: null,
        filesystemErrorCode: null,
        filesystemErrorMessage: null,
      }
      vi.mocked(apiService.getSavedUnmatchedFiles).mockResolvedValueOnce({
        items: [],
        lastScannedAt: undefined,
        warnings: [],
      })
      vi.mocked(apiService.scanUnmatchedFiles).mockResolvedValueOnce({ jobId: 'scan-job-7' })
      vi.mocked(apiService.getUnmatchedResults)
        .mockResolvedValueOnce({
          jobId: 'scan-job-7',
          status: 'Processing',
          items: [],
          warnings: [],
        })
        .mockResolvedValue({
          jobId: 'scan-job-7',
          status: 'Completed',
          items: [],
          warnings: ['One path could not be read and was skipped.'],
        })
      const rootFolder = {
        id: 7,
        name: 'Library',
        path: 'C:\\library',
        isDefault: true,
      } as unknown as RootFolder

      const wrapper = mount(UnmatchedFilesModal, {
        props: { isOpen: false, rootFolder },
        attachTo: document.body,
        global: { plugins: [pinia], stubs: { AddLibraryModal: true } },
      })
      await wrapper.setProps({ isOpen: true })
      await flushPromises()
      const scan = Array.from(document.body.querySelectorAll('button')).find(
        (button) => button.textContent?.trim() === 'Scan',
      )
      expect(scan).toBeTruthy()
      scan!.click()
      await flushPromises()
      expect(document.body.textContent).toContain('Scanning')

      await vi.advanceTimersByTimeAsync(2500)
      await flushPromises()

      expect(document.body.textContent).toContain('All files are in your library')
      expect(document.body.textContent).toContain('One path could not be read and was skipped.')
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })
})
