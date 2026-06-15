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
import type { SearchResult, RootFolder } from '@/types'
import { flushAsync } from '@/test/utils/wait'

const success = vi.fn()
const error = vi.fn()

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success, error }),
}))

describe('LibraryImportFooter', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows a stable importing indicator while the batch is running', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryImportStore()

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
        selectedMatch: { title: 'Book 1', authors: [] } as any as SearchResult,
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
        selectedMatch: { title: 'Book 2', authors: [] } as any as SearchResult,
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
        folders: [{ id: 1, path: 'D:\\library' }] as any as RootFolder[],
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
    await flushAsync()
    await wrapper.vm.$nextTick()

    expect(success).toHaveBeenCalledWith('Import complete', '2 books imported')
  })
})
