import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import RootFolderFormModal from '@/components/settings/RootFolderFormModal.vue'
import MoveAudiobookModal from '@/components/feedback/MoveAudiobookModal.vue'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { apiService } from '@/services/api'

const success = vi.fn()
const warning = vi.fn()
const error = vi.fn()
const filesystemReadinessMock = vi.hoisted(() => ({ filesystemReady: true }))

vi.mock('@/stores/filesystemReadiness', () => ({
  useFilesystemReadinessStore: () => filesystemReadinessMock,
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success, warning, error }),
}))

describe('RootFolderFormModal', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    vi.clearAllMocks()
    filesystemReadinessMock.filesystemReady = true
  })

  it('rejects a Windows drive-relative root before submission', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const create = vi.spyOn(store, 'create')
    const wrapper = mount(RootFolderFormModal, {
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('input[placeholder="Enter a name for this root folder"]').setValue('Library')
    await wrapper.get('#root-path').setValue('C:')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(create).not.toHaveBeenCalled()
    expect(error).toHaveBeenCalledWith(
      'Validation Error',
      expect.stringContaining('separator after the drive letter'),
    )
  })

  it('rejects a relative root before submission', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const create = vi.spyOn(store, 'create')
    const wrapper = mount(RootFolderFormModal, {
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('input[placeholder="Enter a name for this root folder"]').setValue('Library')
    await wrapper.get('#root-path').setValue('relative/library')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(create).not.toHaveBeenCalled()
    expect(error).toHaveBeenCalledWith(
      'Validation Error',
      expect.stringContaining('absolute directory path'),
    )
  })

  it('uses the edited unambiguous path syntax instead of stale root metadata', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(RootFolderFormModal, {
      props: {
        root: {
          id: 8,
          name: 'Migrated Library',
          path: '//server/share/Books',
          pathSyntax: 'Windows',
          isDefault: false,
        },
      },
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('#root-path').setValue('/srv/CON')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(error).not.toHaveBeenCalled()
  })

  it('locks root path repair while filesystem startup reconciliation is unavailable', async () => {
    filesystemReadinessMock.filesystemReady = false
    const pinia = createPinia()
    setActivePinia(pinia)
    const root = {
      id: 12,
      name: 'Unavailable Library',
      path: '/server/mnt/drive/Audiobooks',
      pathSyntax: 'Unix' as const,
      isDefault: false,
      caseSensitivityMode: 'Auto' as const,
      resolvedCaseSensitivity: 'Unknown' as const,
      pathIdentityState: 'Unavailable' as const,
      canChangePath: true,
      canMutateFilesystem: false,
    }
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })

    expect(wrapper.get('#root-path').attributes('disabled')).toBeDefined()
    expect(wrapper.get('#root-case-sensitivity').attributes('disabled')).toBeDefined()
    expect(wrapper.get('.btn-inline-browse').attributes('disabled')).toBeDefined()
  })

  it('guides an unproven Automatic root to the detected case setting', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const root = {
      id: 13,
      name: 'Network Library',
      path: '/library',
      pathSyntax: 'Unix' as const,
      isDefault: true,
      caseSensitivityMode: 'Auto' as const,
      resolvedCaseSensitivity: 'Sensitive' as const,
      pathIdentityState: 'Valid' as const,
      storageState: 'Limited' as const,
      storageReason: 'MutationSemanticsUnproven' as const,
      canChangePath: true,
      canReadFilesystem: true,
      canScanFilesystem: true,
      canMutateFilesystem: false,
    }
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })

    expect(wrapper.text()).toContain('Confirmation needed')
    expect(wrapper.text()).toContain('Use detected setting: case-sensitive')
    expect((wrapper.get('#root-case-sensitivity').element as HTMLSelectElement).value).toBe('Auto')

    await wrapper.get('.detected-semantics-action').trigger('click')

    expect((wrapper.get('#root-case-sensitivity').element as HTMLSelectElement).value).toBe(
      'Sensitive',
    )
    expect(wrapper.text()).toContain(
      'File operations will use this explicitly configured behavior.',
    )
  })

  it('keeps metadata editing available while filesystem path controls are locked', async () => {
    filesystemReadinessMock.filesystemReady = false
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const root = {
      id: 11,
      name: 'Library',
      path: 'C:\\Library',
      pathSyntax: 'Windows' as const,
      isDefault: false,
      caseSensitivityMode: 'Auto' as const,
      resolvedCaseSensitivity: 'Insensitive' as const,
      pathIdentityState: 'Valid' as const,
    }
    store.folders = [root]
    const update = vi.spyOn(store, 'update').mockResolvedValue({
      ...root,
      name: 'Renamed Library',
    })
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })

    expect(wrapper.get('#root-path').attributes('disabled')).toBeDefined()
    expect(wrapper.get('#root-case-sensitivity').attributes('disabled')).toBeDefined()
    expect(wrapper.get('.btn-inline-browse').attributes('disabled')).toBeDefined()
    const name = wrapper.get('input[placeholder="Enter a name for this root folder"]')
    expect(name.attributes('disabled')).toBeUndefined()
    await name.setValue('Renamed Library')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    await vi.waitFor(() => expect(update).toHaveBeenCalledTimes(1))
    expect(update).toHaveBeenCalledWith(
      root.id,
      expect.objectContaining({
        name: 'Renamed Library',
        path: root.path,
        caseSensitivityMode: root.caseSensitivityMode,
      }),
      { expectedCurrentPath: root.path },
    )
  })

  it('updates metadata directly for an equivalent Windows path', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const root = {
      id: 12,
      name: 'Library',
      path: 'C:\\Library',
      pathSyntax: 'Windows' as const,
      isDefault: false,
      caseSensitivityMode: 'Auto' as const,
      resolvedCaseSensitivity: 'Insensitive' as const,
      pathIdentityState: 'Valid' as const,
    }
    store.folders = [root]
    const update = vi.spyOn(store, 'update')
    const updateMetadata = vi.spyOn(apiService, 'updateRootFolder').mockResolvedValue(root)
    const relocate = vi.spyOn(apiService, 'changeRootFolderPath')
    vi.spyOn(apiService, 'getRootFolders').mockResolvedValue([root])
    const wrapper = mount(RootFolderFormModal, {
      props: {
        root,
      },
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('#root-path').setValue('c:/library/')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    await vi.waitFor(() => expect(update).toHaveBeenCalledTimes(1))
    expect(update).toHaveBeenCalledWith(12, expect.objectContaining({ path: 'c:/library/' }), {
      expectedCurrentPath: 'C:\\Library',
    })
    expect(updateMetadata).toHaveBeenCalledWith(
      12,
      expect.objectContaining({ path: 'C:\\Library' }),
    )
    expect(relocate).not.toHaveBeenCalled()
    expect(success).toHaveBeenCalledWith('Success', 'Root folder updated')
  })

  it('ignores stale resolved sensitivity when explicit persisted mode is sensitive', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const root = {
      id: 20,
      name: 'Library',
      path: 'C:\\Library',
      pathSyntax: 'Windows' as const,
      isDefault: false,
      caseSensitivityMode: 'Sensitive' as const,
      resolvedCaseSensitivity: 'Insensitive' as const,
      pathIdentityState: 'Valid' as const,
    }
    store.folders = [root]
    const update = vi.spyOn(store, 'update')
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })
    await wrapper.get('#root-path').setValue('C:\\library')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(update).not.toHaveBeenCalled()
    expect((wrapper.vm as unknown as { showConfirm: boolean }).showConfirm).toBe(true)
  })

  it('fails closed when auto identity is unavailable despite stale insensitive resolution', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const root = {
      id: 21,
      name: 'Library',
      path: 'C:\\Library',
      pathSyntax: 'Windows' as const,
      isDefault: false,
      caseSensitivityMode: 'Auto' as const,
      resolvedCaseSensitivity: 'Insensitive' as const,
      pathIdentityState: 'Unavailable' as const,
    }
    store.folders = [root]
    const update = vi.spyOn(store, 'update')
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })
    await wrapper.get('#root-path').setValue('C:\\library')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(update).not.toHaveBeenCalled()
    expect((wrapper.vm as unknown as { showConfirm: boolean }).showConfirm).toBe(true)
  })

  it('same-path filesystem-semantics repair requires confirmation without offering file movement', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const root = {
      id: 23,
      name: 'Library',
      path: 'D:\\Listenarr Test',
      pathSyntax: 'Windows' as const,
      isDefault: true,
      caseSensitivityMode: 'Auto' as const,
      resolvedCaseSensitivity: 'Sensitive' as const,
      pathIdentityState: 'Valid' as const,
      storageState: 'Unavailable' as const,
      storageReason: 'FilesystemSemanticsChanged' as const,
      canMutateFilesystem: false,
    }
    store.folders = [root]
    const update = vi.spyOn(store, 'update').mockResolvedValue({
      ...root,
      storageState: 'Healthy',
      storageReason: 'None',
      canMutateFilesystem: true,
    })
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()
    await wrapper.vm.$nextTick()

    const moveModal = wrapper.findComponent(MoveAudiobookModal)
    expect(moveModal.props('rootFolderRepair')).toBe(true)
    expect(moveModal.props('showMoveOption')).toBe(false)
    expect(moveModal.props('allowMoveFiles')).toBe(false)
    moveModal.vm.$emit('confirm', { moveFiles: false, deleteEmpty: false })

    await vi.waitFor(() => expect(update).toHaveBeenCalledTimes(1))
    expect(update).toHaveBeenCalledWith(
      root.id,
      expect.objectContaining({ path: root.path }),
      expect.objectContaining({
        expectedCurrentPath: root.path,
        pathChangeConfirmed: true,
        moveFiles: false,
      }),
    )
  })

  it('foreign source path change disables physical move and confirms metadata-only repair', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const root = {
      id: 22,
      name: 'Copied Linux Library',
      path: '/server/mnt/drive/Audiobooks',
      pathSyntax: 'Unix' as const,
      isDefault: true,
      caseSensitivityMode: 'Auto' as const,
      resolvedCaseSensitivity: 'Sensitive' as const,
      pathIdentityState: 'Unavailable' as const,
      storageState: 'Unavailable' as const,
      storageReason: 'ForeignPathSyntax' as const,
      canMutateFilesystem: false,
    }
    store.folders = [root]
    const update = vi.spyOn(store, 'update').mockResolvedValue({
      ...root,
      path: 'D:\\Listenarr Test',
      pathSyntax: 'Windows',
      storageState: 'Healthy',
      storageReason: 'None',
      canMutateFilesystem: true,
    })
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      attachTo: document.body,
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })
    await wrapper.get('#root-path').setValue('D:\\Listenarr Test')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()
    await wrapper.vm.$nextTick()

    const moveFiles = document.body.querySelector<HTMLInputElement>(
      'input[aria-label="Move files now"]',
    )
    expect(moveFiles).not.toBeNull()
    expect(moveFiles!.disabled).toBe(true)
    expect(moveFiles!.checked).toBe(false)
    expect(document.body.textContent).toContain(
      'Files cannot be moved from the current root on this system',
    )
    const moveModal = wrapper.findComponent(MoveAudiobookModal)
    expect(moveModal.props('allowMoveFiles')).toBe(false)
    expect(moveModal.props('moveFiles')).toBe(false)
    moveModal.vm.$emit('confirm', { moveFiles: false, deleteEmpty: false })

    await vi.waitFor(() => expect(update).toHaveBeenCalledTimes(1))
    expect(update).toHaveBeenCalledWith(
      root.id,
      expect.objectContaining({ path: 'D:\\Listenarr Test' }),
      expect.objectContaining({
        expectedCurrentPath: root.path,
        pathChangeConfirmed: true,
        moveFiles: false,
      }),
    )
    wrapper.unmount()
  })

  it('requires relocation confirmation when a sensitive persisted root changes only by case', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const root = {
      id: 14,
      name: 'Library',
      path: 'C:\\Library',
      pathSyntax: 'Windows' as const,
      isDefault: false,
      caseSensitivityMode: 'Sensitive' as const,
      resolvedCaseSensitivity: 'Sensitive' as const,
    }
    store.folders = [root]
    const update = vi.spyOn(store, 'update')
    const updateMetadata = vi.spyOn(apiService, 'updateRootFolder')
    const relocate = vi.spyOn(apiService, 'changeRootFolderPath')
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })
    await wrapper.get('#root-path').setValue('C:\\library')
    await wrapper.get('#root-case-sensitivity').setValue('Insensitive')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect((wrapper.vm as unknown as { showConfirm: boolean }).showConfirm).toBe(true)
    expect(update).not.toHaveBeenCalled()
    expect(updateMetadata).not.toHaveBeenCalled()
    expect(relocate).not.toHaveBeenCalled()
  })

  it('migrates semantics without path confirmation when an insensitive root changes only by case', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const root = {
      id: 18,
      name: 'Library',
      path: 'C:\\Library',
      pathSyntax: 'Windows' as const,
      isDefault: false,
      caseSensitivityMode: 'Insensitive' as const,
      resolvedCaseSensitivity: 'Insensitive' as const,
    }
    const updated = {
      ...root,
      caseSensitivityMode: 'Sensitive' as const,
      resolvedCaseSensitivity: 'Sensitive' as const,
    }
    store.folders = [root]
    const updateMetadata = vi.spyOn(apiService, 'updateRootFolder')
    const relocate = vi.spyOn(apiService, 'changeRootFolderPath').mockResolvedValue({
      relocationId: null,
      rootFolderId: 18,
      currentPath: root.path,
      targetPath: root.path,
      status: 'Completed',
      totalJobs: 0,
      completedJobs: 0,
      targetIdentityEnrollmentState: 'Authorized',
    })
    vi.spyOn(apiService, 'getRootFolders').mockResolvedValue([updated])
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })
    await wrapper.get('#root-path').setValue('C:\\library')
    await wrapper.get('#root-case-sensitivity').setValue('Sensitive')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect((wrapper.vm as unknown as { showConfirm: boolean }).showConfirm).toBe(false)
    expect(updateMetadata).not.toHaveBeenCalled()
    expect(relocate).toHaveBeenCalledWith(18, {
      targetPath: root.path,
      mode: 'metadataOnly',
      deleteEmptySource: false,
      desiredName: root.name,
      desiredIsDefault: false,
      targetCaseSensitivityMode: 'Sensitive',
      expectedCurrentPath: root.path,
    })
  })

  it('fails closed when the current root is missing after reload', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const root = {
      id: 16,
      name: 'Removed Library',
      path: '/removed-library',
      pathSyntax: 'Unix' as const,
      isDefault: false,
    }
    vi.spyOn(apiService, 'getRootFolders').mockResolvedValue([])
    const updateMetadata = vi.spyOn(apiService, 'updateRootFolder')
    const relocate = vi.spyOn(apiService, 'changeRootFolderPath')
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(updateMetadata).not.toHaveBeenCalled()
    expect(relocate).not.toHaveBeenCalled()
    expect(error).toHaveBeenCalledWith('Error', expect.stringContaining('removed'))
  })

  it('requires confirmation for a store-computed path change', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    store.folders = [
      {
        id: 17,
        name: 'Library',
        path: '/old-library',
        pathSyntax: 'Unix',
        isDefault: false,
        resolvedCaseSensitivity: 'Sensitive',
      },
    ]

    await expect(
      store.update(
        17,
        {
          id: 17,
          name: 'Library',
          path: '/new-library',
          isDefault: false,
          caseSensitivityMode: 'Auto',
        },
        { expectedCurrentPath: '/old-library' },
      ),
    ).rejects.toThrow('requires confirmation')
  })

  it('fails closed when the stored root changed while the modal was open', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const root = {
      id: 15,
      name: 'Library',
      path: '/old-library',
      pathSyntax: 'Unix' as const,
      isDefault: false,
    }
    store.folders = [{ ...root, path: '/newer-library' }]
    const updateMetadata = vi.spyOn(apiService, 'updateRootFolder')
    const relocate = vi.spyOn(apiService, 'changeRootFolderPath')
    const wrapper = mount(RootFolderFormModal, {
      props: { root },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(updateMetadata).not.toHaveBeenCalled()
    expect(relocate).not.toHaveBeenCalled()
    expect(error).toHaveBeenCalledWith('Error', expect.stringContaining('changed while editing'))
  })

  it('fails closed for a case-only edit when persisted auto semantics are unknown', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const update = vi.spyOn(store, 'update')
    const wrapper = mount(RootFolderFormModal, {
      props: {
        root: {
          id: 19,
          name: 'Library',
          path: 'C:\\Library',
          pathSyntax: 'Windows',
          isDefault: false,
          caseSensitivityMode: 'Auto',
          resolvedCaseSensitivity: 'Unknown',
        },
      },
      global: {
        plugins: [pinia],
        stubs: { FolderBrowserModal: true },
      },
    })
    await wrapper.get('#root-path').setValue('C:\\library')

    await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

    expect(update).not.toHaveBeenCalled()
    expect((wrapper.vm as unknown as { showConfirm: boolean }).showConfirm).toBe(true)
  })

  it.each(['Sensitive', 'Unknown'] as const)(
    'treats a case-only edit as a path change when sensitive mode resolves as %s',
    async (resolvedCaseSensitivity) => {
      const pinia = createPinia()
      setActivePinia(pinia)
      const store = useRootFoldersStore()
      const update = vi.spyOn(store, 'update')
      const wrapper = mount(RootFolderFormModal, {
        props: {
          root: {
            id: 13,
            name: 'Library',
            path: 'C:\\Library',
            pathSyntax: 'Windows',
            isDefault: false,
            caseSensitivityMode: 'Sensitive',
            resolvedCaseSensitivity,
          },
        },
        global: {
          plugins: [pinia],
          stubs: {
            FolderBrowserModal: true,
          },
        },
      })
      await wrapper.get('#root-path').setValue('C:\\library')

      await (wrapper.vm as unknown as { save: () => Promise<void> }).save()

      expect(update).not.toHaveBeenCalled()
      expect((wrapper.vm as unknown as { showConfirm: boolean }).showConfirm).toBe(true)
    },
  )

  it('shows the structured root-folder conflict message without the raw API wrapper', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    const publicMessage =
      'This root folder already has a path change in progress. Wait for it to finish, or resolve and retry the existing relocation before changing the path again.'
    vi.spyOn(store, 'update').mockRejectedValue(
      Object.assign(new Error(`API error: 409 {"message":"${publicMessage}"}`), {
        status: 409,
        body: JSON.stringify({
          message: publicMessage,
          code: 'root_folder_relocation_active',
        }),
      }),
    )
    const wrapper = mount(RootFolderFormModal, {
      props: {
        root: {
          id: 7,
          name: 'Library',
          path: '/old-library',
          isDefault: true,
        },
      },
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('#root-path').setValue('/new-library')

    await (
      wrapper.vm as unknown as { confirmChange: (moveFiles: boolean) => Promise<void> }
    ).confirmChange(false)

    expect(error).toHaveBeenCalledWith('Error', publicMessage)
    expect(error).not.toHaveBeenCalledWith('Error', expect.stringContaining('API error: 409'))
  })

  it('reports metadata-only partial success as a warning instead of a failed save', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    vi.spyOn(store, 'update').mockResolvedValue({
      id: 7,
      name: 'Library',
      path: '/new-library',
      isDefault: true,
      activeRelocation: {
        relocationId: 'repair-1',
        rootFolderId: 7,
        currentPath: '/new-library',
        targetPath: '/new-library',
        status: 'NeedsAttention',
        totalJobs: 2,
        completedJobs: 1,
        error: 'The relocation requires attention.',
        targetIdentityEnrollmentState: 'Authorized',
      },
    })
    const wrapper = mount(RootFolderFormModal, {
      props: {
        root: {
          id: 7,
          name: 'Library',
          path: '/old-library',
          isDefault: true,
        },
      },
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('#root-path').setValue('/new-library')

    await (
      wrapper.vm as unknown as { confirmChange: (moveFiles: boolean) => Promise<void> }
    ).confirmChange(false)

    expect(warning).toHaveBeenCalledWith(
      'Root folder changed',
      expect.stringContaining('audiobooks still need path repair'),
    )
    expect(error).not.toHaveBeenCalled()
    expect(success).not.toHaveBeenCalled()
  })

  it.each([
    [true, 'Root relocation started'],
    [false, 'Root folder changed'],
  ])('reports the path change accurately when moveFiles is %s', async (moveFiles, message) => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useRootFoldersStore()
    vi.spyOn(store, 'update').mockResolvedValue({
      id: 7,
      name: 'Library',
      path: '/new-library',
      isDefault: true,
    })
    const wrapper = mount(RootFolderFormModal, {
      props: {
        root: {
          id: 7,
          name: 'Library',
          path: '/old-library',
          isDefault: true,
        },
      },
      global: {
        plugins: [pinia],
        stubs: {
          FolderBrowserModal: true,
        },
      },
    })
    await wrapper.get('#root-path').setValue('/new-library')
    await (
      wrapper.vm as unknown as { confirmChange: (moveFiles: boolean) => Promise<void> }
    ).confirmChange(moveFiles)
    await vi.waitFor(() => expect(success).toHaveBeenCalledWith('Success', message))
    expect(store.update).toHaveBeenCalledWith(
      7,
      expect.objectContaining({ path: '/new-library' }),
      expect.objectContaining({
        expectedCurrentPath: '/old-library',
        pathChangeConfirmed: true,
        moveFiles,
      }),
    )
  })
})
