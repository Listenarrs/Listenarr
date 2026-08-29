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
import { beforeEach, describe, it, expect, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import RootFoldersSettings from '@/components/settings/RootFoldersSettings.vue'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { useToast } from '@/services/toastService'
import type { RootFolder, RootFolderPathChangeResult } from '@/types'

const targetPath = '/srv/Audiobooks '

function relocation(
  targetIdentityEnrollmentState: RootFolderPathChangeResult['targetIdentityEnrollmentState'],
): RootFolderPathChangeResult {
  return {
    relocationId: 'relocation-1',
    rootFolderId: 3,
    currentPath: '/srv/Old',
    targetPath,
    status: 'NeedsAttention',
    totalJobs: 1,
    completedJobs: 0,
    error: 'Authorization required',
    targetIdentityEnrollmentState,
    mode: 'Relocate',
  }
}

function rootFolder(activeRelocation: RootFolderPathChangeResult | null): RootFolder {
  return {
    id: 3,
    name: 'Audiobooks',
    path: '/srv/Old',
    isDefault: true,
    pathIdentityState: 'Valid',
    resolvedCaseSensitivity: 'Sensitive',
    storageState: 'Healthy',
    storageReason: 'None',
    canConfirmCurrentFolder: false,
    canChangePath: true,
    canReadFilesystem: true,
    canScanFilesystem: true,
    canMutateFilesystem: true,
    activeRelocation,
  }
}

function createReadyPinia() {
  const pinia = createPinia()
  setActivePinia(pinia)
  useFilesystemReadinessStore().readiness = {
    isReady: true,
    status: 'ready',
    databaseConnected: true,
    migrationsCurrent: true,
    filesystemReady: true,
    filesystemStatus: 'Ready',
  }
  return pinia
}

describe('RootFoldersSettings', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    vi.clearAllMocks()
    vi.mocked(apiService.getRootFolders).mockReset().mockResolvedValue([])
    vi.mocked(apiService.getRootFolderMetadataRepairDetails).mockReset()
    vi.mocked(apiService.removeRootFolderMetadataRepairFile).mockReset()
    vi.mocked(apiService.abandonUnpublishedRootFolderRelocation).mockReset()
    vi.mocked(apiService.retryRootFolderRelocation).mockReset()
    useToast().toasts.splice(0)
  })

  it('shows header spinner and loading state when store.loading is true', async () => {
    const pinia = createReadyPinia()

    useRootFoldersStore()

    // Make the underlying API call pending so store.loading remains true while mounted
    const api = await import('@/services/api')
    let resolveFn: (value: unknown) => void = () => {}
    // spy on the apiService instance method (module-level named export is not present in TS types)
    vi.spyOn((api as unknown).apiService, 'getRootFolders').mockImplementation(
      () =>
        new Promise((res) => {
          resolveFn = res
        }) as unknown,
    )

    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    // Wait for onMounted to run and for store.load() to set loading=true
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.loading-state').exists()).toBe(true)
    expect(wrapper.find('.section-header .small-inline-spinner').exists()).toBe(true)

    // Resolve API and ensure UI updates
    resolveFn([])
    await new Promise((r) => setTimeout(r, 0))
    await wrapper.vm.$nextTick()
  })

  it('offers a one-click detected case setting for mutation-limited network storage', async () => {
    const unprovenRoot: RootFolder = {
      ...rootFolder(null),
      caseSensitivityMode: 'Auto',
      resolvedCaseSensitivity: 'Sensitive',
      storageState: 'Limited',
      storageReason: 'MutationSemanticsUnproven',
      storageMessage: 'Automatic case semantics need confirmation.',
      canMutateFilesystem: false,
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([unprovenRoot])
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const store = useRootFoldersStore()
    const update = vi.spyOn(store, 'update').mockResolvedValue({
      ...unprovenRoot,
      caseSensitivityMode: 'Sensitive',
      storageState: 'Healthy',
      storageReason: 'None',
      canMutateFilesystem: true,
    })

    expect(wrapper.text()).toContain('Needs case setting')
    expect(wrapper.text()).toContain('Use detected setting: case-sensitive')

    await wrapper.get('[data-cy="mutation-semantics-guidance"] button').trigger('click')
    await flushPromises()

    expect(update).toHaveBeenCalledWith(
      unprovenRoot.id,
      expect.objectContaining({
        id: unprovenRoot.id,
        path: unprovenRoot.path,
        caseSensitivityMode: 'Sensitive',
      }),
      { expectedCurrentPath: unprovenRoot.path },
    )
  })

  it('presents partial metadata repair as an actionable progress panel', async () => {
    const skippedAudiobookIds = [8, 10, 11, 12, 13, 14, 15, 18, 19, 20, 21, 22, 23, 32]
    const active = {
      ...relocation('Authorized'),
      mode: 'MetadataOnly' as const,
      currentPath: targetPath,
      targetPath,
      totalJobs: 81,
      completedJobs: 67,
      skippedAudiobookIds,
      skippedItems: skippedAudiobookIds.map((audiobookId) => ({
        audiobookId,
        reasonCode:
          audiobookId === 32
            ? ('TargetIdentityCollision' as const)
            : ('InvalidStoredPath' as const),
      })),
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([
      {
        ...rootFolder(active),
        path: targetPath,
      },
    ])
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Path repair needs attention')
    expect(wrapper.text()).toContain('67 of 81 audiobooks updated')
    expect(wrapper.text()).toContain('14 audiobooks still need manual review')
    expect(wrapper.text()).not.toContain('Pending path:')
    expect(wrapper.text()).not.toContain('Authorization required')
    expect(wrapper.get('[role="progressbar"]').attributes('aria-valuenow')).toBe('83')
    expect(wrapper.get('.relocation-affected summary').text()).toContain(
      '14 audiobooks need attention',
    )
    expect(wrapper.get('a[href="/audiobooks/32"]').text()).toBe('Audiobook #32')
    expect(wrapper.text()).toContain('Tracked file paths collide at this destination.')
    expect(wrapper.text()).not.toContain('Case-sensitive file paths collide')
    expect(
      wrapper.findAll('button').some((button) => button.text().trim() === 'Retry remaining'),
    ).toBe(true)
  })

  it('loads collision repair details and removes only the selected tracked record', async () => {
    const active = {
      ...relocation('Unavailable'),
      mode: 'MetadataOnly' as const,
      currentPath: targetPath,
      targetPath,
      totalJobs: 1,
      completedJobs: 0,
      skippedAudiobookIds: [32],
      skippedItems: [
        {
          audiobookId: 32,
          reasonCode: 'TargetIdentityCollision' as const,
        },
      ],
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([
      {
        ...rootFolder(active),
        path: targetPath,
      },
    ])
    vi.mocked(apiService.getRootFolderMetadataRepairDetails).mockResolvedValue({
      relocationId: 'relocation-1',
      audiobookId: 32,
      audiobookTitle: 'Powerless',
      reasonCode: 'TargetIdentityCollision',
      collisionGroups: [
        {
          targetRelativePath: 'Author/Powerless/book.mp3',
          files: [
            { audiobookFileId: 100, audiobookId: 32, relativePath: 'book.mp3', canRemove: true },
            { audiobookFileId: 101, audiobookId: 32, relativePath: 'book.MP3', canRemove: true },
          ],
        },
      ],
    })
    vi.mocked(apiService.removeRootFolderMetadataRepairFile).mockResolvedValue({
      relocationId: 'relocation-1',
      audiobookId: 32,
      audiobookTitle: 'Powerless',
      reasonCode: 'TargetIdentityCollision',
      collisionGroups: [],
    })
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const review = wrapper.findAll('button').find((button) => button.text().trim() === 'Review')
    expect(review).toBeDefined()
    await review!.trigger('click')
    await flushPromises()

    expect(apiService.getRootFolderMetadataRepairDetails).toHaveBeenCalledWith('relocation-1', 32)
    expect(wrapper.text()).toContain('Powerless')
    expect(wrapper.text()).toContain('book.mp3')
    expect(wrapper.text()).toContain('book.MP3')

    const remove = wrapper
      .findAll('button')
      .find((button) => button.text().trim() === 'Remove tracked record')
    expect(remove).toBeDefined()
    await remove!.trigger('click')
    await wrapper.vm.$nextTick()
    const confirm = wrapper
      .findAll('button')
      .find((button) => button.text().includes('Remove record'))
    expect(confirm).toBeDefined()
    await confirm!.trigger('click')
    await flushPromises()

    expect(apiService.removeRootFolderMetadataRepairFile).toHaveBeenCalledWith(
      'relocation-1',
      32,
      100,
    )
    expect(wrapper.text()).toContain('No remaining conflicting tracked file records were found')
  })

  it('reloads root relocation state after SignalR reconnect', async () => {
    const active = relocation('Authorized')
    vi.mocked(apiService.getRootFolders)
      .mockResolvedValueOnce([rootFolder(active)])
      .mockResolvedValueOnce([rootFolder(null)])
    let connected: (() => void) | undefined
    const unsubscribe = vi.fn()
    vi.spyOn(signalRService, 'onConnected').mockImplementation((callback) => {
      connected = callback
      return unsubscribe
    })
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Library move needs attention')
    connected?.()
    await flushPromises()

    expect(apiService.getRootFolders).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).not.toContain('Library move needs attention')
    wrapper.unmount()
    expect(unsubscribe).toHaveBeenCalledTimes(1)
  })

  it.each([
    ['Healthy', 'Healthy', true, false, true],
    ['Limited', 'Limited', false, false, true],
    ['Missing', 'Missing', false, false, false],
    ['Unavailable', 'Unavailable', false, false, false],
    ['Unconfirmed', 'Needs confirmation', false, true, false],
  ] as const)(
    'renders %s storage state with the correct actions',
    async (
      storageState,
      label,
      canMutateFilesystem,
      canConfirmCurrentFolder,
      canScanFilesystem,
    ) => {
      const folder = {
        ...rootFolder(null),
        storageState,
        storageReason:
          storageState === 'Healthy'
            ? ('None' as const)
            : storageState === 'Missing'
              ? ('PathMissing' as const)
              : storageState === 'Unconfirmed'
                ? ('NoAuthorizedIdentity' as const)
                : storageState === 'Limited'
                  ? ('IdentityUnsupported' as const)
                  : ('AccessDenied' as const),
        storageMessage:
          storageState === 'Healthy' ? null : `Storage is ${storageState.toLowerCase()}.`,
        canMutateFilesystem,
        canConfirmCurrentFolder,
        canScanFilesystem,
        confirmationToken: canConfirmCurrentFolder ? 'observation-token' : null,
      }
      vi.mocked(apiService.getRootFolders).mockResolvedValue([folder])
      const pinia = createReadyPinia()
      const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
      await flushPromises()

      expect(wrapper.text()).toContain(label)
      expect(wrapper.get('[data-cy="scan-unmatched"]').attributes('disabled') !== undefined).toBe(
        !canScanFilesystem,
      )
      expect(wrapper.find('[data-cy="confirm-root-folder"]').exists()).toBe(canConfirmCurrentFolder)
      wrapper.unmount()
    },
  )

  it('shows expandable technical storage details without replacing the user-facing message', async () => {
    const folder = {
      ...rootFolder(null),
      storageState: 'Limited' as const,
      storageReason: 'IdentityUnsupported' as const,
      storageMessage:
        'This storage can be read and scanned, but it does not expose the durable file identity required for crash-safe moves and deletions.',
      storageDetail:
        'statx omitted birth time and name_to_handle_at returned operation not permitted.',
      canReadFilesystem: true,
      canScanFilesystem: true,
      canMutateFilesystem: false,
      canConfirmCurrentFolder: false,
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([folder])
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.text()).toContain('read and scanned')
    const details = wrapper.get('.storage-detail')
    expect(details.text()).toContain('Technical storage details')
    expect(details.text()).toContain('statx omitted birth time')
    wrapper.unmount()
  })

  it('states that weak-storage moves copy and retain the source', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValue([
      {
        ...rootFolder(null),
        storageState: 'Limited',
        storageReason: 'IdentityUnsupported',
        storageMessage: 'Durable file identity is unavailable.',
        canPublishNewFiles: true,
        canMutateFilesystem: false,
      },
    ])
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const policy = wrapper.get('[data-cy="compatibility-publication-message"]')
    expect(policy.text()).toContain('will copy files into this storage and retain the source')
    expect(policy.text()).toContain('will not attempt source cleanup')
    wrapper.unmount()
  })

  it('shows initializing, blocks filesystem actions, and keeps metadata editing available', async () => {
    const folder = {
      ...rootFolder(null),
      storageState: 'Initializing' as const,
      storageReason: 'Initializing' as const,
      storageMessage: 'Library filesystem initialization is in progress.',
      canMutateFilesystem: false,
      canChangePath: false,
      canConfirmCurrentFolder: false,
      confirmationToken: null,
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([folder])
    const pinia = createPinia()
    setActivePinia(pinia)
    useFilesystemReadinessStore().readiness = {
      isReady: true,
      status: 'ready',
      databaseConnected: true,
      migrationsCurrent: true,
      filesystemReady: false,
      filesystemStatus: 'Running',
      filesystemPhase: 'AudiobookFileIdentities',
    }
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Initializing')
    expect(wrapper.text()).not.toContain('Needs confirmation')
    expect(wrapper.get('[data-cy="scan-unmatched"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-cy="edit-root-folder"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.find('[data-cy="confirm-root-folder"]').exists()).toBe(false)
  })

  it('confirms the exact observed folder generation only when confirmation is available', async () => {
    const folder = {
      ...rootFolder(null),
      storageState: 'Changed' as const,
      storageReason: 'IdentityMismatch' as const,
      storageMessage: 'The folder at this location changed.',
      canConfirmCurrentFolder: true,
      canMutateFilesystem: false,
      confirmationToken: 'observation-token',
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([folder])
    vi.mocked(apiService.confirmRootFolder).mockResolvedValue(folder)
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Folder changed')
    const action = wrapper.get('[data-cy="confirm-root-folder"]')
    await action.trigger('click')

    const displayedPath = wrapper.get('[data-testid="root-folder-confirmation-path"]')
    expect(displayedPath.element.textContent).toBe(folder.path)
    const confirm = wrapper.get('.modal-delete-button')
    expect(confirm.text()).toContain('Confirm folder')
    await confirm.trigger('click')
    await flushPromises()

    expect(apiService.confirmRootFolder).toHaveBeenCalledWith(
      folder.id,
      folder.path,
      folder.confirmationToken,
    )
  })

  it('blocks metadata-only retry while startup filesystem reconciliation is unavailable', async () => {
    const active = {
      ...relocation('Authorized'),
      mode: 'MetadataOnly' as const,
      skippedAudiobookIds: [32],
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(active)])
    const pinia = createPinia()
    setActivePinia(pinia)
    useFilesystemReadinessStore().readiness = {
      isReady: true,
      status: 'ready',
      databaseConnected: true,
      migrationsCurrent: true,
      filesystemReady: false,
      filesystemStatus: 'Failed',
    }
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const retry = wrapper
      .findAll('button')
      .find((button) => button.text().trim() === 'Retry remaining')
    expect(retry).toBeDefined()
    expect(retry!.attributes('disabled')).toBeDefined()
  })

  it('keeps failed metadata repair retry available without physical target identity once startup is ready', async () => {
    const active = {
      ...relocation('Unavailable'),
      mode: 'MetadataOnly' as const,
      status: 'Failed' as const,
      error: 'Metadata recovery failed.',
      skippedAudiobookIds: [],
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(active)])
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Path repair failed')
    const retry = wrapper
      .findAll('button')
      .find((button) => button.text().trim() === 'Retry repair')
    expect(retry).toBeDefined()
    expect(retry!.attributes('disabled')).toBeUndefined()
  })

  it('offers cancel only when the backend marks an unpublished physical relocation abandonable', async () => {
    const abandonable = {
      ...relocation('Authorized'),
      status: 'NeedsAttention' as const,
      totalJobs: 1,
      completedJobs: 0,
      canAbandon: true,
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(abandonable)])
    vi.mocked(apiService.abandonUnpublishedRootFolderRelocation).mockResolvedValue({
      ...abandonable,
      status: 'Failed',
      canAbandon: false,
    })
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const buttons = wrapper.findAll('button')
    const cancel = buttons.find((button) => button.text().trim() === 'Cancel unfinished')
    expect(cancel).toBeDefined()
    expect(buttons.some((button) => button.text().trim() === 'Retry')).toBe(false)
    await cancel!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('No audiobook move jobs were published')

    const confirm = wrapper
      .findAll('button')
      .find((button) => button.text().trim() === 'Cancel relocation')
    expect(confirm).toBeDefined()
    await confirm!.trigger('click')
    await flushPromises()

    expect(apiService.abandonUnpublishedRootFolderRelocation).toHaveBeenCalledWith(
      abandonable.relocationId,
    )
    expect(useToast().toasts[0]?.title).toBe('Root relocation canceled')
  })

  it('does not infer abandon authority from a physical NeedsAttention status alone', async () => {
    const active = {
      ...relocation('Authorized'),
      status: 'NeedsAttention' as const,
      totalJobs: 1,
      completedJobs: 0,
      canAbandon: false,
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(active)])
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.text()).not.toContain('Cancel unfinished')
  })

  it('reports a durable failed retry result as a failure, not partial attention', async () => {
    const active = {
      ...relocation('Unavailable'),
      mode: 'MetadataOnly' as const,
      status: 'Failed' as const,
      error: 'Metadata recovery failed.',
      skippedAudiobookIds: [],
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(active)])
    vi.mocked(apiService.retryRootFolderRelocation).mockResolvedValue({
      ...active,
      error:
        'The relocation failed. Review the server logs and retry after resolving the underlying issue.',
    })
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const retry = wrapper
      .findAll('button')
      .find((button) => button.text().trim() === 'Retry repair')
    expect(retry).toBeDefined()
    await retry!.trigger('click')
    await flushPromises()

    const retryToast = useToast().toasts[0]
    expect(retryToast?.level).toBe('error')
    expect(retryToast?.title).toBe('Path repair failed')
    expect(retryToast?.message).toContain('The relocation failed')
    expect(retryToast?.message).not.toContain('still needs attention')
  })

  it('shows the structured API message when retry is rejected', async () => {
    const active = {
      ...relocation('Unavailable'),
      mode: 'MetadataOnly' as const,
      status: 'Failed' as const,
      error: 'Metadata recovery failed.',
      skippedAudiobookIds: [],
    }
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(active)])
    const apiError = Object.assign(
      new Error('API error: 409 {"message":"Resolve the active recovery state before retrying."}'),
      {
        status: 409,
        body: JSON.stringify({
          message: 'Resolve the active recovery state before retrying.',
          code: 'root_folder_path_change_blocked',
        }),
      },
    )
    vi.mocked(apiService.retryRootFolderRelocation).mockRejectedValue(apiError)
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const retry = wrapper
      .findAll('button')
      .find((button) => button.text().trim() === 'Retry repair')
    expect(retry).toBeDefined()
    await retry!.trigger('click')
    await flushPromises()

    const retryToast = useToast().toasts[0]
    expect(retryToast?.title).toBe('Retry failed')
    expect(retryToast?.message).toBe('Resolve the active recovery state before retrying.')
    expect(retryToast?.message).not.toContain('API error')
    expect(retryToast?.message).not.toContain('409')
  })

  it('disables set-default while a relocation owns the root metadata state', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValue([
      {
        ...rootFolder(relocation('Authorized')),
        isDefault: false,
      },
    ])
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const setDefault = wrapper.get('button[title="Set as Default"]')
    expect(setDefault.attributes('disabled')).toBeDefined()
    await setDefault.trigger('click')
    expect(apiService.updateRootFolder).not.toHaveBeenCalled()
  })

  it('keeps ordinary retry separate for an authorized relocation', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(relocation('Authorized'))])
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.find('[data-cy="confirm-root-folder"]').exists()).toBe(false)
    expect(wrapper.findAll('button').some((button) => button.text().trim() === 'Retry')).toBe(true)
  })

  it('fails closed when the target identity is unavailable', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(relocation('Unavailable'))])
    const pinia = createReadyPinia()
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.find('[data-cy="confirm-root-folder"]').exists()).toBe(false)
    expect(wrapper.findAll('button').some((button) => button.text().trim() === 'Retry')).toBe(false)
  })
})
