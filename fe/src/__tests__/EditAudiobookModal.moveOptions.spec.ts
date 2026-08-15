/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { mount } from '@vue/test-utils'
import { vi, describe, it, expect, beforeEach } from 'vitest'

type MoveJobUpdate = { jobId?: string; status?: string; target?: string; error?: string }

const toastMocks = vi.hoisted(() => ({
  info: vi.fn(),
  success: vi.fn(),
  error: vi.fn(),
}))

const filesystemReadinessMock = vi.hoisted(() => ({
  filesystemReady: true,
  filesystemInitializing: false,
  filesystemFailed: false,
}))

const signalRMocks = vi.hoisted(() => {
  const state = {
    callback: null as ((job: MoveJobUpdate) => void) | null,
    unsubscribe: vi.fn(),
    onMoveJobUpdate: vi.fn(),
  }
  state.onMoveJobUpdate.mockImplementation((callback: (job: MoveJobUpdate) => void) => {
    state.callback = callback
    return state.unsubscribe
  })
  return state
})

vi.mock('@/services/api', () => ({
  apiService: {
    getAudiobook: vi.fn().mockImplementation(async (id: number) => ({ id })),
    getQualityProfiles: vi.fn().mockResolvedValue([]),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
    getAudiobookIdentifiers: vi.fn().mockResolvedValue({ identifiers: [] }),
    checkVolume: vi.fn().mockResolvedValue({ sameVolume: true }),
    updateAudiobook: vi.fn().mockResolvedValue({ message: 'ok', audiobook: {} }),
    updateAudiobookIdentifiers: vi.fn().mockResolvedValue({ identifiers: [] }),
    getMoveRecoveryState: vi.fn().mockResolvedValue({
      hasUnresolvedMove: false,
      disposition: 'None',
      jobId: null,
      status: null,
      phase: null,
      requestedPath: null,
      error: null,
      canRetry: false,
      blockingJobIds: [],
    }),
    requeueMoveJob: vi.fn().mockImplementation(async (jobId: string) => ({
      message: 'requeued',
      jobId,
    })),
    getMoveJobStatus: vi.fn().mockImplementation(async (jobId: string) => ({
      jobId,
      audiobookId: 1,
      status: 'Queued',
    })),
    moveAudiobook: vi.fn().mockImplementation(async (_id: number, destination: string) => ({
      message: 'queued',
      jobId: 'job-1',
      target: destination,
    })),
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => toastMocks,
}))

vi.mock('@/stores/filesystemReadiness', () => ({
  useFilesystemReadinessStore: () => filesystemReadinessMock,
}))

vi.mock('@/services/signalr', () => ({
  signalRService: {
    onMoveJobUpdate: signalRMocks.onMoveJobUpdate,
  },
}))

import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'

const audiobook = {
  id: 1,
  title: 'Sample',
  authors: ['Author'],
  basePath: 'C:\\root\\Some Author\\Some Title',
  imageUrl: 'C:\\root\\Some Author\\Some Title\\cover.jpg',
  monitored: true,
  tags: [],
}

describe('EditAudiobookModal move options', () => {
  beforeEach(async () => {
    vi.clearAllMocks()
    filesystemReadinessMock.filesystemReady = true
    filesystemReadinessMock.filesystemInitializing = false
    filesystemReadinessMock.filesystemFailed = false
    signalRMocks.callback = null
    signalRMocks.onMoveJobUpdate.mockImplementation((callback: (job: MoveJobUpdate) => void) => {
      signalRMocks.callback = callback
      return signalRMocks.unsubscribe
    })
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getMoveRecoveryState).mockResolvedValue({
      hasUnresolvedMove: false,
      disposition: 'None',
      jobId: null,
      status: null,
      phase: null,
      requestedPath: null,
      error: null,
      canRetry: false,
      blockingJobIds: [],
    })
    vi.mocked(apiService.requeueMoveJob).mockImplementation(async (jobId: string) => ({
      message: 'requeued',
      jobId,
    }))
    vi.mocked(apiService.getMoveJobStatus).mockImplementation(async (jobId: string) => ({
      jobId,
      audiobookId: 1,
      status: 'Queued',
    }))
    vi.mocked(apiService.moveAudiobook).mockImplementation(
      async (_id: number, destination: string) => ({
        message: 'queued',
        jobId: 'job-1',
        target: destination,
      }),
    )
  })

  it('disables destination edits and move resume while filesystem initialization is running', async () => {
    const { apiService } = await import('@/services/api')
    filesystemReadinessMock.filesystemReady = false
    filesystemReadinessMock.filesystemInitializing = true
    vi.mocked(apiService.getMoveRecoveryState).mockResolvedValue({
      hasUnresolvedMove: true,
      disposition: 'RetryAvailable',
      jobId: 'recover-job-initializing',
      status: 'Failed',
      phase: 'Published',
      requestedPath: 'C:\\root\\Recovered Author\\Recovered Book',
      error: 'Interrupted move detected.',
      canRetry: true,
      blockingJobIds: ['recover-job-initializing'],
    })

    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })
    await new Promise((resolve) => setTimeout(resolve, 200))

    expect(wrapper.get('.btn-edit-destination').attributes('disabled')).toBeDefined()
    const resume = wrapper.get('[data-testid="resume-move-button"]')
    expect(resume.attributes('disabled')).toBeDefined()
    await resume.trigger('click')
    expect(apiService.requeueMoveJob).not.toHaveBeenCalled()
  })

  it('rehydrates an interrupted move after a fresh open and resumes the original job', async () => {
    const { apiService } = await import('@/services/api')
    const recoverable = {
      hasUnresolvedMove: true,
      disposition: 'RetryAvailable',
      jobId: 'recover-job-1',
      status: 'Failed',
      phase: 'Published',
      requestedPath: 'C:\\root\\Recovered Author\\Recovered Book',
      error: 'The previous filesystem cleanup was interrupted.',
      canRetry: true,
      blockingJobIds: ['recover-job-1'],
    }
    vi.mocked(apiService.getMoveRecoveryState)
      .mockResolvedValueOnce(recoverable)
      .mockResolvedValueOnce({
        ...recoverable,
        disposition: 'InProgress',
        status: 'Queued',
        canRetry: false,
      })

    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 200))

    const notice = wrapper.get('[data-testid="move-recovery-notice"]')
    expect(notice.text()).toContain('An interrupted move needs to be resumed.')
    expect(notice.text()).toContain('C:\\root\\Recovered Author\\Recovered Book')
    expect(wrapper.get('.btn-edit-destination').attributes('disabled')).toBeDefined()

    await wrapper.get('[data-testid="resume-move-button"]').trigger('click')
    await new Promise((resolve) => setTimeout(resolve, 50))

    expect(apiService.requeueMoveJob).toHaveBeenCalledWith('recover-job-1')
    expect(toastMocks.info).toHaveBeenCalledWith(
      'Move resumed',
      'Move job recover-job-1 was queued to resume its interrupted work.',
    )
  })

  it('turns a race-time recovery conflict into resumable server state instead of retrying a fresh move', async () => {
    const { apiService } = await import('@/services/api')
    const noRecovery = {
      hasUnresolvedMove: false,
      disposition: 'None',
      jobId: null,
      status: null,
      phase: null,
      requestedPath: null,
      error: null,
      canRetry: false,
      blockingJobIds: [] as string[],
    }
    const recoverable = {
      hasUnresolvedMove: true,
      disposition: 'RetryAvailable',
      jobId: 'recover-job-race',
      status: 'Failed',
      phase: 'Published',
      requestedPath: 'C:\\root\\New Author\\New Book',
      error: 'Interrupted move detected.',
      canRetry: true,
      blockingJobIds: ['recover-job-race'],
    }
    vi.mocked(apiService.getMoveRecoveryState).mockImplementation(async () =>
      vi.mocked(apiService.moveAudiobook).mock.calls.length > 0 ? recoverable : noRecovery,
    )
    vi.mocked(apiService.moveAudiobook).mockRejectedValueOnce(
      Object.assign(new Error('API error'), {
        status: 409,
        body: JSON.stringify({
          code: 'move_recovery_required',
          message: 'An interrupted move still owns this audiobook filesystem state.',
          jobId: 'recover-job-race',
          status: 'Failed',
          requestedPath: recoverable.requestedPath,
          recoveryDisposition: 'RetryAvailable',
          canRetry: true,
        }),
      }),
    )

    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })
    await new Promise((resolve) => setTimeout(resolve, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    await wrapper.vm.$nextTick()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((resolve) => setTimeout(resolve, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise
    await wrapper.vm.$nextTick()

    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.requeueMoveJob).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="move-recovery-notice"]').text()).toContain(
      'An interrupted move needs to be resumed.',
    )
    expect(wrapper.get('[data-testid="resume-move-button"]').exists()).toBe(true)
    expect(toastMocks.error).toHaveBeenCalledWith(
      'Resume interrupted move',
      'An interrupted move still owns this audiobook filesystem state.',
    )
  })

  it('Change without moving should persist metadata and identifiers before the destination update', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    // let init settle
    await new Promise((r) => setTimeout(r, 200))

    // Ensure there is a detectable destination change under the configured output root.
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    ;(wrapper.vm as unknown).formData.title = 'Sample Updated'
    ;(wrapper.vm as unknown).formData.identifiers = [
      {
        localKey: 'new-asin',
        type: 'Asin',
        value: 'B0TEST1234',
        region: 'us',
        isPrimary: true,
        source: 'Manual',
      },
    ]
    await wrapper.vm.$nextTick()

    // Start save flow and resolve the in-component confirmation promise by
    // calling the module-scoped resolver if it was created. This avoids
    // relying on modal rendering in jsdom.
    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: false, deleteEmptySource: false })
    await savePromise
    // Allow async work to settle
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.moveAudiobook).toHaveBeenCalledWith(1, 'C:\\root\\New Author\\New Book', {
      sourcePath: 'C:\\root\\Some Author\\Some Title',
      moveFiles: false,
      deleteEmptySource: false,
    })
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(1)
    const updatePayload = vi.mocked(apiService.updateAudiobook).mock.calls[0][1] as Record<
      string,
      unknown
    >
    expect(updatePayload.title).toBe('Sample Updated')
    expect(Object.prototype.hasOwnProperty.call(updatePayload, 'basePath')).toBe(false)
    expect(Object.prototype.hasOwnProperty.call(updatePayload, 'imageUrl')).toBe(false)
    expect(apiService.updateAudiobookIdentifiers).toHaveBeenCalledTimes(1)
    expect(vi.mocked(apiService.updateAudiobook).mock.invocationCallOrder[0]).toBeLessThan(
      vi.mocked(apiService.updateAudiobookIdentifiers).mock.invocationCallOrder[0],
    )
    expect(
      vi.mocked(apiService.updateAudiobookIdentifiers).mock.invocationCallOrder[0],
    ).toBeLessThan(vi.mocked(apiService.moveAudiobook).mock.invocationCallOrder[0])
  })

  it('reports partial success when metadata saves but the destination update fails', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.moveAudiobook).mockRejectedValueOnce(new Error('queue unavailable'))
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    ;(wrapper.vm as unknown).formData.title = 'Saved Before Move Failure'
    await wrapper.vm.$nextTick()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise

    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.objectContaining({ title: 'Saved Before Move Failure' }),
    )
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(1)
    expect(toastMocks.error).toHaveBeenCalledWith(
      'Move failed',
      'Your metadata changes were saved, but the destination update could not be confirmed.',
    )
    expect(wrapper.emitted('saved')).toBeUndefined()
  })

  it('shows a structured destination rejection inline with the effective path', async () => {
    const { apiService } = await import('@/services/api')
    const rejectedPath = 'C:\\root\\New Author\\New Book'
    vi.mocked(apiService.moveAudiobook).mockRejectedValueOnce(
      Object.assign(new Error('API error'), {
        status: 400,
        body: JSON.stringify({
          code: 'destination_path_outside_roots',
          field: 'destinationPath',
          message: 'DestinationPath must be inside a configured root folder or output path',
          resolvedDestination: rejectedPath,
        }),
      }),
    )
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    await wrapper.vm.$nextTick()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((resolve) => setTimeout(resolve, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise
    await wrapper.vm.$nextTick()

    expect(wrapper.get('[data-testid="effective-destination"]').text()).toContain(rejectedPath)
    expect(wrapper.text()).toContain(
      'DestinationPath must be inside a configured root folder or output path',
    )
    expect(toastMocks.error).toHaveBeenCalledWith(
      'Invalid destination',
      'DestinationPath must be inside a configured root folder or output path',
    )
    expect(wrapper.emitted('saved')).toBeUndefined()
  })

  it('shows the server source-manifest failure instead of directing the user to a nonexistent queue job', async () => {
    const { apiService } = await import('@/services/api')
    const manifestMessage =
      'The audiobook has no validated tracked files. Rescan or repair it before moving files.'
    vi.mocked(apiService.moveAudiobook).mockRejectedValueOnce(
      Object.assign(new Error('API error'), {
        status: 400,
        body: JSON.stringify({
          code: 'move_source_unverified',
          field: 'sourcePath',
          message: manifestMessage,
        }),
      }),
    )
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    await wrapper.vm.$nextTick()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((resolve) => setTimeout(resolve, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise

    expect(toastMocks.error).toHaveBeenCalledWith('Move failed', manifestMessage)
    expect(wrapper.emitted('saved')).toBeUndefined()
  })

  it('Destination-only change without moving should call move API and skip metadata update', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    await wrapper.vm.$nextTick()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: false, deleteEmptySource: true })
    await savePromise
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(0)
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.moveAudiobook).toHaveBeenCalledWith(1, 'C:\\root\\New Author\\New Book', {
      sourcePath: 'C:\\root\\Some Author\\Some Title',
      moveFiles: false,
      deleteEmptySource: false,
    })
  })

  it('Move should call move API with deleteEmptySource true by default', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))

    // Ensure there is a detectable destination change and flip monitored.
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    ;(wrapper.vm as unknown).formData.monitored = false
    await wrapper.vm.$nextTick()

    // Start save flow and resolve the in-component confirmation promise to
    // simulate the user choosing to move files now.
    const savePromise2 = (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver2 = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver2) resolver2({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise2

    // Wait for async update + move to settle
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.not.objectContaining({ basePath: expect.anything() }),
    )
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.moveAudiobook).toHaveBeenCalledWith(1, 'C:\\root\\New Author\\New Book', {
      sourcePath: 'C:\\root\\Some Author\\Some Title',
      moveFiles: true,
      deleteEmptySource: true,
    })
  })

  it('rooted destination input cannot bypass the relative-only action boundary', async () => {
    const { apiService } = await import('@/services/api')
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'C:\\root\\New Author\\New Book'
    ;(wrapper.vm as unknown).formData.title = 'Should Not Save'
    await wrapper.vm.$nextTick()

    await (wrapper.vm as unknown).handleSave()

    expect(apiService.updateAudiobook).not.toHaveBeenCalled()
    expect(apiService.moveAudiobook).not.toHaveBeenCalled()
    expect(toastMocks.error).toHaveBeenCalledWith(
      'Invalid destination',
      'Enter a path relative to the selected configured root folder.',
    )
  })

  it('Destination with parent traversal should be invalid and not call save APIs', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'Some Author\\Some Title\\..'
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain('Path traversal is not allowed in the destination folder')
    expect(
      wrapper.find('button[aria-label="Save destination"]').attributes('disabled'),
    ).toBeDefined()

    await (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(0)
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(0)
  })

  it('Destination segment with trailing whitespace should be invalid and not call save APIs', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'Some Author\\Some Title\\test '
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain(
      'Windows destination folder segments cannot end with a space or period',
    )
    expect(
      wrapper.find('button[aria-label="Save destination"]').attributes('disabled'),
    ).toBeDefined()

    await (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(0)
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(0)
  })

  it('Destination inside current source should be allowed as a content move', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'Some Author\\Some Title\\ test'
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).not.toContain('Source and destination folders cannot overlap')
    expect(
      wrapper.find('button[aria-label="Save destination"]').attributes('disabled'),
    ).toBeUndefined()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(0)
    expect(apiService.moveAudiobook).toHaveBeenCalledWith(
      1,
      'C:\\root\\Some Author\\Some Title\\ test',
      {
        sourcePath: 'C:\\root\\Some Author\\Some Title',
        moveFiles: true,
        deleteEmptySource: true,
      },
    )
  })

  it('Windows destination segment with leading whitespace outside source should be allowed', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'Some Author\\Other Title\\ test'
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).not.toContain('Windows destination folder segments cannot end')
    expect(
      wrapper.find('button[aria-label="Save destination"]').attributes('disabled'),
    ).toBeUndefined()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.moveAudiobook).toHaveBeenCalledWith(
      1,
      'C:\\root\\Some Author\\Other Title\\ test',
      {
        sourcePath: 'C:\\root\\Some Author\\Some Title',
        moveFiles: true,
        deleteEmptySource: true,
      },
    )
  })

  it('blocks a duplicate destination change while this client is already tracking an active move', async () => {
    const pinia = (await import('pinia')).createPinia()
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [pinia] },
    })

    await new Promise((resolve) => setTimeout(resolve, 200))
    const { useMoveJobsStore } = await import('@/stores/moveJobs')
    const moveJobsStore = useMoveJobsStore(pinia)
    moveJobsStore.trackQueuedJob({
      jobId: 'job-active',
      audiobookId: audiobook.id,
      status: 'Running',
      target: 'C:\\root\\First Destination',
    })
    ;(wrapper.vm as unknown).formData.relativePath = 'Second Destination'
    await wrapper.vm.$nextTick()
    await (wrapper.vm as unknown).handleSave()

    const { apiService } = await import('@/services/api')
    expect(apiService.moveAudiobook).not.toHaveBeenCalled()
    expect(toastMocks.info).toHaveBeenCalledWith(
      'Move already in progress',
      'Move job job-active is still running. Wait for it to finish before changing the destination again.',
    )
  })

  it('Move-only destination changes should enqueue move without pre-saving BasePath', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    await wrapper.vm.$nextTick()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    const { useMoveJobsStore } = await import('@/stores/moveJobs')
    const moveJobsStore = useMoveJobsStore()
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(0)
    expect(apiService.moveAudiobook).toHaveBeenCalledWith(1, 'C:\\root\\New Author\\New Book', {
      sourcePath: 'C:\\root\\Some Author\\Some Title',
      moveFiles: true,
      deleteEmptySource: true,
    })
    expect(moveJobsStore.trackedById['job-1']).toEqual({
      jobId: 'job-1',
      audiobookId: 1,
      status: 'Queued',
      progress: 0,
      phase: undefined,
      target: 'C:\\root\\New Author\\New Book',
      error: undefined,
      recoveryDisposition: undefined,
      canRetry: undefined,
    })
    expect(signalRMocks.onMoveJobUpdate).toHaveBeenCalledTimes(1)
    expect(wrapper.emitted('saved')).toHaveLength(1)
    expect(wrapper.emitted('close')).toHaveLength(1)
  })

  it('tracks the server-authoritative resolved move destination', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.moveAudiobook).mockResolvedValueOnce({
      message: 'queued',
      jobId: 'job-canonical',
      target: 'C:/root/Canonical Author/Canonical Book',
    })
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    await wrapper.vm.$nextTick()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((resolve) => setTimeout(resolve, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise

    const { useMoveJobsStore } = await import('@/stores/moveJobs')
    const moveJobsStore = useMoveJobsStore()
    expect(moveJobsStore.trackedById['job-canonical']?.target).toBe(
      'C:/root/Canonical Author/Canonical Book',
    )
  })

  it('preserves legal whitespace in the server-authoritative move destination', async () => {
    const { apiService } = await import('@/services/api')
    const serverTarget = '/library/Author/Book '
    vi.mocked(apiService.moveAudiobook).mockResolvedValueOnce({
      message: 'queued',
      jobId: 'job-whitespace',
      target: serverTarget,
    })
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    await wrapper.vm.$nextTick()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((resolve) => setTimeout(resolve, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise

    const { useMoveJobsStore } = await import('@/stores/moveJobs')
    const moveJobsStore = useMoveJobsStore()
    expect(moveJobsStore.trackedById['job-whitespace']?.target).toBe(serverTarget)
  })

  it('rejects an untrackable physical move response', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.moveAudiobook).mockResolvedValueOnce({ message: 'queued' })
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 200))
    ;(wrapper.vm as unknown).formData.relativePath = 'New Author\\New Book'
    await wrapper.vm.$nextTick()

    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((resolve) => setTimeout(resolve, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise

    const { useMoveJobsStore } = await import('@/stores/moveJobs')
    const moveJobsStore = useMoveJobsStore()
    expect(Object.keys(moveJobsStore.trackedById)).not.toContain('undefined')
    expect(wrapper.emitted('saved')).toBeUndefined()
    expect(toastMocks.error).toHaveBeenCalledWith(
      'Move failed',
      'The destination update could not be confirmed. No move job was created.',
    )
  })

  it('legacy out-of-root audiobooks allow metadata-only saves until relocation is explicitly chosen', async () => {
    const { apiService } = await import('@/services/api')
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          basePath: 'D:\\legacy\\Some Author\\Some Title',
        },
      },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 200))
    ;(wrapper.vm as unknown).formData.title = 'Legacy Metadata Updated'
    await wrapper.vm.$nextTick()

    await (wrapper.vm as unknown).handleSave()

    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.objectContaining({ title: 'Legacy Metadata Updated' }),
    )
    expect(apiService.moveAudiobook).not.toHaveBeenCalled()
    expect(wrapper.emitted('saved')).toHaveLength(1)
  })

  it('Edition-only changes should persist through updateAudiobook', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))
    ;(wrapper.vm as unknown as { formData: { edition: string } }).formData.edition =
      'Revised Edition'
    await wrapper.vm.$nextTick()

    await (wrapper.vm as unknown as { handleSave: () => Promise<void> }).handleSave()
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.objectContaining({ edition: 'Revised Edition' }),
    )
  })

  it('metadata edit with separator-only configured-root path does not enqueue a move', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))

    const vm = wrapper.vm as unknown as {
      formData: { title: string; relativePath: string }
      handleSave: () => Promise<void>
    }
    vm.formData.relativePath = 'Some Author/Some Title'
    vm.formData.title = 'Updated Sample'
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).not.toContain('Destination folder must be different')

    await vm.handleSave()
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.objectContaining({ title: 'Updated Sample' }),
    )
    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.not.objectContaining({ basePath: expect.anything() }),
    )
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(0)
  })

  it('metadata changes should persist through updateAudiobook', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          subtitle: 'Original Subtitle',
          narrators: ['Original Narrator'],
          description: 'Original description',
          publisher: 'Original Publisher',
          language: 'english',
          publishedDate: '2024-01-15',
          publishYear: '2024',
          runtime: 600,
          series: 'Original Series',
          seriesNumber: '1',
          genres: ['Fantasy'],
          imageUrl: 'https://example.com/original.jpg',
        },
      },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))

    const vm = wrapper.vm as unknown as {
      formData: {
        title: string
        subtitle: string
        authors: string[]
        narrators: string[]
        description: string
        publisher: string
        language: string
        publishedDate: string
        publishYear: string
        runtime: string
        edition: string
        seriesMemberships: Array<{
          seriesName: string
          seriesNumber: string
          isPrimary: boolean
        }>
        genres: string[]
        imageUrl: string
      }
      handleSave: () => Promise<void>
    }

    vm.formData.title = 'Edited Title'
    vm.formData.subtitle = 'Edited Subtitle'
    vm.formData.authors = ['Edited Author']
    vm.formData.narrators = ['Edited Narrator']
    vm.formData.description = 'Edited description'
    vm.formData.publisher = 'Edited Publisher'
    vm.formData.language = 'swedish'
    vm.formData.publishedDate = '2025-02-01'
    vm.formData.publishYear = '2025'
    vm.formData.runtime = '720'
    vm.formData.edition = 'Collector Edition'
    vm.formData.seriesMemberships = [
      { seriesName: 'Edited Universe', seriesNumber: '4', isPrimary: true },
      { seriesName: 'Anthology Line', seriesNumber: '12', isPrimary: false },
    ]
    vm.formData.genres = ['Sci-Fi', 'Adventure']
    vm.formData.imageUrl = 'https://example.com/edited.jpg'
    await wrapper.vm.$nextTick()

    await vm.handleSave()
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.objectContaining({
        title: 'Edited Title',
        subtitle: 'Edited Subtitle',
        authors: ['Edited Author'],
        narrators: ['Edited Narrator'],
        description: 'Edited description',
        publisher: 'Edited Publisher',
        language: 'Swedish',
        publishedDate: '2025-02-01',
        publishYear: '2025',
        runtime: 720,
        edition: 'Collector Edition',
        series: 'Edited Universe',
        seriesNumber: '4',
        seriesMemberships: [
          expect.objectContaining({
            seriesName: 'Edited Universe',
            seriesNumber: '4',
            isPrimary: true,
            sortOrder: 0,
          }),
          expect.objectContaining({
            seriesName: 'Anthology Line',
            seriesNumber: '12',
            isPrimary: false,
            sortOrder: 1,
          }),
        ],
        genres: ['Sci-Fi', 'Adventure'],
        imageUrl: 'https://example.com/edited.jpg',
      }),
    )
  })

  it('persists a non-first primary series selection (regression for #658)', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))

    const vm = wrapper.vm as unknown as {
      formData: {
        seriesMemberships: Array<{ seriesName: string; seriesNumber: string; isPrimary: boolean }>
      }
      handleSave: () => Promise<void>
    }

    // User marks the SECOND series as primary; the bug previously reverted this to the first.
    vm.formData.seriesMemberships = [
      { seriesName: 'Publication Order', seriesNumber: '1', isPrimary: false },
      { seriesName: 'Chronological Order', seriesNumber: '3', isPrimary: true },
    ]
    await wrapper.vm.$nextTick()

    await vm.handleSave()
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.objectContaining({
        series: 'Chronological Order',
        seriesNumber: '3',
        seriesMemberships: [
          expect.objectContaining({
            seriesName: 'Publication Order',
            isPrimary: false,
            sortOrder: 0,
          }),
          expect.objectContaining({
            seriesName: 'Chronological Order',
            isPrimary: true,
            sortOrder: 1,
          }),
        ],
      }),
    )
  })

  it('hydrates current metadata immediately and renders person fields as tags', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getQualityProfiles).mockImplementation(() => new Promise(() => {}))
    vi.mocked(apiService.getAudiobook).mockResolvedValue({
      ...audiobook,
      subtitle: 'Existing Subtitle',
      narrators: ['Narrator One', 'Narrator Two'],
      description: 'Existing description',
      publisher: 'Existing Publisher',
      language: 'english',
      publishedDate: '2024-01-15',
      publishYear: '2024',
      runtime: 600,
      edition: 'First Edition',
      series: 'Existing Series',
      seriesNumber: '3',
      seriesMemberships: [
        { seriesName: 'Existing Series', seriesNumber: '3', isPrimary: true, sortOrder: 0 },
        { seriesName: 'Universe Collection', seriesNumber: '9', isPrimary: false, sortOrder: 1 },
      ],
      genres: ['Fantasy', 'Adventure'],
      imageUrl: 'https://example.com/current.jpg',
      tags: ['favorite'],
    })

    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 50))

    expect(wrapper.text()).toContain('Edit Audiobook: Sample')
    expect((wrapper.get('#metadata-title').element as HTMLInputElement).value).toBe('Sample')
    expect((wrapper.get('#metadata-subtitle').element as HTMLInputElement).value).toBe(
      'Existing Subtitle',
    )
    expect((wrapper.get('#metadata-description').element as HTMLTextAreaElement).value).toBe(
      'Existing description',
    )
    expect((wrapper.get('#metadata-publisher').element as HTMLInputElement).value).toBe(
      'Existing Publisher',
    )
    expect((wrapper.get('#metadata-language').element as HTMLInputElement).value).toBe('English')
    expect((wrapper.get('#metadata-published-date').element as HTMLInputElement).value).toBe(
      '2024-01-15',
    )

    const authorTags = wrapper.findAll('.author-tags-editor .tag-item').map((item) => item.text())
    expect(authorTags).toEqual(expect.arrayContaining(['Author']))

    const narratorTags = wrapper
      .findAll('.narrator-tags-editor .tag-item')
      .map((item) => item.text())
    expect(narratorTags).toEqual(expect.arrayContaining(['Narrator One', 'Narrator Two']))

    const genreTags = wrapper.findAll('.genre-tags-editor .tag-item').map((item) => item.text())
    expect(genreTags).toEqual(expect.arrayContaining(['Fantasy', 'Adventure']))
    expect((wrapper.get('#metadata-series-name-0').element as HTMLInputElement).value).toBe(
      'Existing Series',
    )
    expect((wrapper.get('#metadata-series-number-1').element as HTMLInputElement).value).toBe('9')
  })

  it('rehydrates unchanged metadata when the same audiobook receives fuller data', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getAudiobook).mockResolvedValue({
      ...audiobook,
      description: 'Loaded from refreshed detail payload',
      publishedDate: '2024-03-01',
      language: 'english',
      narrators: ['Narrator One'],
    })

    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 50))

    await wrapper.setProps({
      audiobook: {
        ...audiobook,
        description: 'Loaded from refreshed detail payload',
        language: 'english',
        narrators: ['Narrator One'],
      },
    })
    await new Promise((r) => setTimeout(r, 50))

    expect((wrapper.get('#metadata-description').element as HTMLTextAreaElement).value).toBe(
      'Loaded from refreshed detail payload',
    )
    expect((wrapper.get('#metadata-published-date').element as HTMLInputElement).value).toBe(
      '2024-03-01',
    )
    expect((wrapper.get('#metadata-language').element as HTMLInputElement).value).toBe('English')
    expect(wrapper.findAll('.narrator-tags-editor .tag-item').map((item) => item.text())).toEqual(
      expect.arrayContaining(['Narrator One']),
    )
  })
})
