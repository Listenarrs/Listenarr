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
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LibraryImportFooter from '@/components/domain/audiobook/LibraryImportFooter.vue'
import { useLibraryImportStore } from '@/stores/libraryImport'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import type { SearchResult, RootFolder } from '@/types'

const success = vi.fn()
const error = vi.fn()
const warning = vi.fn()

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success, error, warning }),
}))

describe('LibraryImportFooter', () => {
  beforeEach(() => {
    vi.clearAllMocks()
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

    vi.spyOn(store, 'importSelected').mockImplementation(
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

    const importButton = wrapper.find('button.btn.btn-primary')
    await importButton.trigger('click')
    await wrapper.vm.$nextTick()

    expect(importButton.text()).toContain('Importing 2 Books...')
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
})
