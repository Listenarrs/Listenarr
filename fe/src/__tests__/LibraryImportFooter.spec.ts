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
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LibraryImportFooter from '@/components/domain/audiobook/LibraryImportFooter.vue'
import { useLibraryImportStore } from '@/stores/libraryImport'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import { apiService } from '@/services/api'
import type { QualityProfile, SearchResult, RootFolder } from '@/types'

const success = vi.fn()
const error = vi.fn()
const warning = vi.fn()

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success, error, warning }),
}))

describe('LibraryImportFooter', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(apiService, 'getQualityProfiles').mockResolvedValue([
      { id: 2, name: 'Standard', isDefault: true },
      { id: 3, name: 'Lossless', isDefault: false },
    ] as QualityProfile[])
  })

  it('shows a stable importing indicator while the batch is running', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryImportStore()
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

    let resolveImport: ((value: { imported: number; errors: string[] }) => void) | null = null

    store.items = {
      'C:\\incoming\\Book 1.mp3': {
        id: 'C:\\incoming\\Book 1.mp3',
        fullPath: 'C:\\incoming\\Book 1.mp3',
        sourceFiles: ['C:\\incoming\\Book 1.mp3'],
        folderPath: 'C:\\incoming',
        relativePath: 'Book 1',
        folderName: 'Book 1',
        format: 'MP3',
        fileCount: 1,
        selectedMatch: { title: 'Book 1', authors: [] } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
      'C:\\incoming\\Book 2.mp3': {
        id: 'C:\\incoming\\Book 2.mp3',
        fullPath: 'C:\\incoming\\Book 2.mp3',
        sourceFiles: ['C:\\incoming\\Book 2.mp3'],
        folderPath: 'C:\\incoming',
        relativePath: 'Book 2',
        folderName: 'Book 2',
        format: 'MP3',
        fileCount: 1,
        selectedMatch: { title: 'Book 2', authors: [] } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }

    const importSelected = vi.spyOn(store, 'importSelected').mockImplementation(
      () =>
        new Promise<{ imported: number; errors: string[] }>((resolve) => {
          resolveImport = resolve
        }),
    )

    const wrapper = mount(LibraryImportFooter, {
      props: {
        folders: [{ id: 1, path: 'D:\\library' }] as unknown as RootFolder[],
      },
      global: {
        plugins: [pinia],
      },
    })

    await flushPromises()
    const profileSelect = wrapper.get('select:has(option[value="3"])')
    expect((profileSelect.element as HTMLSelectElement).value).toBe('2')
    await profileSelect.setValue('3')

    const importButton = wrapper.find('button.btn.btn-primary')
    await importButton.trigger('click')
    await wrapper.vm.$nextTick()

    expect(importButton.text()).toContain('Importing 2 Books...')
    expect(importSelected).toHaveBeenCalledWith('', 3)
    expect((importButton.element as HTMLButtonElement).disabled).toBe(true)

    resolveImport?.({ imported: 2, errors: [] })
    await new Promise((resolve) => setTimeout(resolve, 0))
    await wrapper.vm.$nextTick()

    expect(success).toHaveBeenCalledWith('Import complete', '2 books imported')
  })

  it('disables cached-result imports while filesystem initialization is incomplete', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryImportStore()
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
    store.items = {
      'C:\\incoming\\Book.mp3': {
        id: 'C:\\incoming\\Book.mp3',
        fullPath: 'C:\\incoming\\Book.mp3',
        sourceFiles: ['C:\\incoming\\Book.mp3'],
        folderPath: 'C:\\incoming',
        relativePath: 'Book',
        folderName: 'Book',
        format: 'MP3',
        fileCount: 1,
        selectedMatch: { title: 'Book', authors: [] } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    const importSelected = vi.spyOn(store, 'importSelected')
    const wrapper = mount(LibraryImportFooter, {
      props: {
        folders: [{ id: 1, path: 'D:\\library' }] as unknown as RootFolder[],
      },
      global: { plugins: [pinia] },
    })

    const importButton = wrapper.get('button.btn.btn-primary')
    expect(importButton.attributes('disabled')).toBeDefined()
    expect(importButton.attributes('title')).toContain('filesystem initialization')
    await importButton.trigger('click')
    expect(importSelected).not.toHaveBeenCalled()
  })

  it('discloses copy-and-retain policy before a move from weak storage', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryImportStore()
    store.action = 'move'

    const wrapper = mount(LibraryImportFooter, {
      props: {
        folders: [
          {
            id: 1,
            path: 'D:\\library',
            canPublishNewFiles: true,
            canMutateFilesystem: true,
          },
        ] as unknown as RootFolder[],
        sourceFolder: {
          id: 2,
          path: '\\\\nas\\audiobooks',
          canPublishNewFiles: true,
          canMutateFilesystem: false,
        } as unknown as RootFolder,
      },
      global: { plugins: [pinia] },
    })

    const policy = wrapper.get('[data-testid="move-policy-warning"]')
    expect(policy.text()).toContain('Move will copy the selected files and retain the source')
    expect(policy.text()).toContain('will not attempt source cleanup')

    store.action = 'hardlink/copy'
    await wrapper.vm.$nextTick()
    expect(wrapper.find('[data-testid="move-policy-warning"]').exists()).toBe(false)
  })

  it('disables Import while a matching search or a Find Match search is running', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryImportStore()
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
    store.items = {
      '/incoming/Book 1.mp3': {
        id: '/incoming/Book 1.mp3',
        fullPath: '/incoming/Book 1.mp3',
        sourceFiles: ['/incoming/Book 1.mp3'],
        folderPath: '/incoming',
        relativePath: 'Book 1',
        folderName: 'Book 1',
        format: 'MP3',
        fileCount: 1,
        selectedMatch: { title: 'Book 1', authors: [] } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    const wrapper = mount(LibraryImportFooter, {
      props: { folders: [{ id: 1, path: '/library' }] as unknown as RootFolder[] },
      global: { plugins: [pinia] },
    })
    const importButton = () => wrapper.find('button.btn.btn-primary').element as HTMLButtonElement
    expect(importButton().disabled).toBe(false)

    store.findMatchSearchStarted()
    await wrapper.vm.$nextTick()
    expect(importButton().disabled).toBe(true)
    store.findMatchSearchEnded()
    await wrapper.vm.$nextTick()
    expect(importButton().disabled).toBe(false)

    store.isProcessing = true
    await wrapper.vm.$nextTick()
    expect(importButton().disabled).toBe(true)
  })

  it('asks for another destination folder when the saved one no longer exists', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryImportStore()
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
    store.action = 'move'
    // Chosen on an earlier visit, then removed in Settings > Root Folders
    store.destinationFolderId = 9
    store.items = {
      'C:\\incoming\\Book.mp3': {
        id: 'C:\\incoming\\Book.mp3',
        fullPath: 'C:\\incoming\\Book.mp3',
        sourceFiles: ['C:\\incoming\\Book.mp3'],
        folderPath: 'C:\\incoming',
        relativePath: 'Book',
        folderName: 'Book',
        format: 'MP3',
        fileCount: 1,
        selectedMatch: { title: 'Book', authors: [] } as unknown as SearchResult,
        hasSearched: true,
        isSearching: false,
        selected: true,
      },
    }
    const importSelected = vi
      .spyOn(store, 'importSelected')
      .mockResolvedValue({ imported: 1, errors: [] })

    const wrapper = mount(LibraryImportFooter, {
      props: {
        folders: [
          { id: 1, path: 'D:\\library' },
          { id: 2, path: 'E:\\other' },
        ] as unknown as RootFolder[],
      },
      global: { plugins: [pinia] },
    })
    await flushPromises()

    // No folder is chosen in its place: Move must not silently go to another library folder
    const destination = wrapper.get('select.destination-select')
    expect((destination.element as HTMLSelectElement).selectedOptions[0]?.text).toBe(
      'Select a folder',
    )
    const importButton = wrapper.get('button.btn.btn-primary')
    expect(importButton.attributes('disabled')).toBeDefined()
    expect(importButton.attributes('title')).toBe('Select the folder to import into')
    await importButton.trigger('click')
    expect(importSelected).not.toHaveBeenCalled()

    await destination.setValue('2')
    expect(importButton.attributes('disabled')).toBeUndefined()
    await importButton.trigger('click')
    expect(importSelected).toHaveBeenCalledWith('E:\\other', 2)
  })

  it('asks for another quality profile when the saved one no longer exists', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryImportStore()
    // Chosen on an earlier visit, then removed in Settings > Quality Profiles
    store.qualityProfileId = 9

    const wrapper = mount(LibraryImportFooter, {
      props: { folders: [{ id: 1, path: 'D:\\library' }] as unknown as RootFolder[] },
      global: { plugins: [pinia] },
    })
    await flushPromises()

    // Not the default profile in its place: new books must not get another profile unnoticed
    const profileSelect = wrapper.get('select:has(option[value="3"])')
    expect((profileSelect.element as HTMLSelectElement).selectedOptions[0]?.text).toBe(
      'Select a quality profile',
    )
    await profileSelect.setValue('3')
    expect(store.qualityProfileId).toBe(3)
  })

  describe('when root folders or quality profiles changed since the choice was made', () => {
    // A store with one book ready to import, files moved, as left by an earlier visit
    function storeWithChoices(choices: {
      destinationFolderId?: number | null
      qualityProfileId?: number | null
    }) {
      const pinia = createPinia()
      setActivePinia(pinia)
      const store = useLibraryImportStore()
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
      store.action = 'move'
      store.destinationFolderId = choices.destinationFolderId ?? null
      store.qualityProfileId = choices.qualityProfileId ?? null
      store.items = {
        '/incoming/Book.mp3': {
          id: '/incoming/Book.mp3',
          fullPath: '/incoming/Book.mp3',
          sourceFiles: ['/incoming/Book.mp3'],
          folderPath: '/incoming',
          relativePath: 'Book',
          folderName: 'Book',
          format: 'MP3',
          fileCount: 1,
          selectedMatch: { title: 'Book', authors: [] } as unknown as SearchResult,
          hasSearched: true,
          isSearching: false,
          selected: true,
        },
      }
      const importSelected = vi
        .spyOn(store, 'importSelected')
        .mockResolvedValue({ imported: 1, errors: [] })
      return { pinia, store, importSelected }
    }

    async function mountFooter(pinia: ReturnType<typeof createPinia>, folders: object[]) {
      const wrapper = mount(LibraryImportFooter, {
        props: { folders: folders as unknown as RootFolder[] },
        global: { plugins: [pinia] },
      })
      await flushPromises()
      return wrapper
    }

    function selectedText(wrapper: Awaited<ReturnType<typeof mountFooter>>, selector: string) {
      return (wrapper.get(selector).element as HTMLSelectElement).selectedOptions[0]?.text.trim()
    }

    const profileSelector = 'select:has(option[value="3"])'

    it('keeps the chosen folder when a folder was added', async () => {
      const { pinia, importSelected } = storeWithChoices({ destinationFolderId: 2 })

      const wrapper = await mountFooter(pinia, [
        { id: 4, name: 'New', path: '/new' },
        { id: 1, name: 'Incoming', path: '/incoming' },
        { id: 2, name: 'Library', path: '/library' },
      ])

      expect(selectedText(wrapper, 'select.destination-select')).toBe('/library')
      await wrapper.get('button.btn.btn-primary').trigger('click')
      expect(importSelected).toHaveBeenCalledWith('/library', 2)
    })

    it('keeps the first folder when a folder was added after it and none was chosen', async () => {
      const { pinia, importSelected } = storeWithChoices({})

      const wrapper = await mountFooter(pinia, [
        { id: 1, name: 'Incoming', path: '/incoming' },
        { id: 2, name: 'Library', path: '/library' },
        { id: 4, name: 'New', path: '/new' },
      ])

      expect(selectedText(wrapper, 'select.destination-select')).toBe('/incoming')
      await wrapper.get('button.btn.btn-primary').trigger('click')
      expect(importSelected).toHaveBeenCalledWith('/incoming', 2)
    })

    it('keeps the chosen folder when it was renamed', async () => {
      const { pinia, importSelected } = storeWithChoices({ destinationFolderId: 2 })

      const wrapper = await mountFooter(pinia, [
        { id: 1, name: 'Incoming', path: '/incoming' },
        { id: 2, name: 'Main library', path: '/library' },
      ])

      expect(selectedText(wrapper, 'select.destination-select')).toBe('/library')
      await wrapper.get('button.btn.btn-primary').trigger('click')
      expect(importSelected).toHaveBeenCalledWith('/library', 2)
    })

    it('imports into the new path of the chosen folder when its path changed', async () => {
      const { pinia, importSelected } = storeWithChoices({ destinationFolderId: 2 })

      const wrapper = await mountFooter(pinia, [
        { id: 1, name: 'Incoming', path: '/incoming' },
        { id: 2, name: 'Library', path: '/mnt/library' },
      ])

      expect(selectedText(wrapper, 'select.destination-select')).toBe('/mnt/library')
      await wrapper.get('button.btn.btn-primary').trigger('click')
      expect(importSelected).toHaveBeenCalledWith('/mnt/library', 2)
    })

    it('follows a change of the folder list while the page is open', async () => {
      const { pinia, importSelected } = storeWithChoices({ destinationFolderId: 2 })
      const wrapper = await mountFooter(pinia, [
        { id: 1, name: 'Incoming', path: '/incoming' },
        { id: 2, name: 'Library', path: '/library' },
      ])

      await wrapper.setProps({
        folders: [
          { id: 4, name: 'New', path: '/new' },
          { id: 1, name: 'Incoming', path: '/incoming' },
          { id: 2, name: 'Renamed', path: '/mnt/library' },
        ] as unknown as RootFolder[],
      })

      expect(selectedText(wrapper, 'select.destination-select')).toBe('/mnt/library')
      await wrapper.get('button.btn.btn-primary').trigger('click')
      expect(importSelected).toHaveBeenCalledWith('/mnt/library', 2)
    })

    it('keeps the chosen quality profile when a profile was added', async () => {
      vi.spyOn(apiService, 'getQualityProfiles').mockResolvedValue([
        { id: 4, name: 'Archive', isDefault: false },
        { id: 2, name: 'Standard', isDefault: true },
        { id: 3, name: 'Lossless', isDefault: false },
      ] as QualityProfile[])
      const { pinia, importSelected } = storeWithChoices({
        destinationFolderId: 1,
        qualityProfileId: 3,
      })

      const wrapper = await mountFooter(pinia, [{ id: 1, name: 'Library', path: '/library' }])

      expect(selectedText(wrapper, profileSelector)).toBe('Lossless')
      await wrapper.get('button.btn.btn-primary').trigger('click')
      expect(importSelected).toHaveBeenCalledWith('/library', 3)
    })

    it('uses the new default quality profile when none was chosen', async () => {
      vi.spyOn(apiService, 'getQualityProfiles').mockResolvedValue([
        { id: 2, name: 'Standard', isDefault: false },
        { id: 3, name: 'Lossless', isDefault: false },
        { id: 4, name: 'Archive', isDefault: true },
      ] as QualityProfile[])
      const { pinia, importSelected } = storeWithChoices({ destinationFolderId: 1 })

      const wrapper = await mountFooter(pinia, [{ id: 1, name: 'Library', path: '/library' }])

      expect(selectedText(wrapper, profileSelector)).toBe('Archive (Default)')
      await wrapper.get('button.btn.btn-primary').trigger('click')
      expect(importSelected).toHaveBeenCalledWith('/library', 4)
    })

    it('waits for a quality profile when the chosen one was removed', async () => {
      // Sending no profile would make the server give new books its default profile unnoticed
      const { pinia, store, importSelected } = storeWithChoices({
        destinationFolderId: 1,
        qualityProfileId: 9,
      })
      store.action = 'none'

      const wrapper = await mountFooter(pinia, [{ id: 1, name: 'Library', path: '/library' }])

      const importButton = wrapper.get('button.btn.btn-primary')
      expect(importButton.attributes('disabled')).toBeDefined()
      expect(importButton.attributes('title')).toBe('Select the quality profile for new books')
      await importButton.trigger('click')
      expect(importSelected).not.toHaveBeenCalled()

      await wrapper.get(profileSelector).setValue('3')
      expect(importButton.attributes('disabled')).toBeUndefined()
      expect(importButton.attributes('title')).toBeUndefined()
      await importButton.trigger('click')
      expect(importSelected).toHaveBeenCalledWith('', 3)
    })

    it('still imports books already in the library when no quality profile is left', async () => {
      // No select to choose from: the server refuses new books itself, existing ones need none
      vi.spyOn(apiService, 'getQualityProfiles').mockResolvedValue([])
      const { pinia, store, importSelected } = storeWithChoices({
        destinationFolderId: 1,
        qualityProfileId: 9,
      })
      store.action = 'none'

      const wrapper = await mountFooter(pinia, [{ id: 1, name: 'Library', path: '/library' }])

      expect(wrapper.text()).toContain('To import new books, set up a profile')
      const importButton = wrapper.get('button.btn.btn-primary')
      expect(importButton.attributes('disabled')).toBeUndefined()
      await importButton.trigger('click')
      expect(importSelected).toHaveBeenCalledWith('', undefined)
    })

    it('keeps the chosen quality profile, under its new name, when it was renamed', async () => {
      vi.spyOn(apiService, 'getQualityProfiles').mockResolvedValue([
        { id: 2, name: 'Standard', isDefault: true },
        { id: 3, name: 'Lossless FLAC', isDefault: false },
      ] as QualityProfile[])
      const { pinia, importSelected } = storeWithChoices({
        destinationFolderId: 1,
        qualityProfileId: 3,
      })

      const wrapper = await mountFooter(pinia, [{ id: 1, name: 'Library', path: '/library' }])

      expect(selectedText(wrapper, profileSelector)).toBe('Lossless FLAC')
      await wrapper.get('button.btn.btn-primary').trigger('click')
      expect(importSelected).toHaveBeenCalledWith('/library', 3)
    })
  })
})
