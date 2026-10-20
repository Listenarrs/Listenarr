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
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia, type Pinia } from 'pinia'

const routeQuery = vi.hoisted(() => ({ value: {} as Record<string, string> }))
const routerReplace = vi.hoisted(() => vi.fn())
vi.mock('vue-router', () => ({
  useRoute: () => ({ query: routeQuery.value }),
  useRouter: () => ({ replace: routerReplace, push: vi.fn() }),
}))

import LibraryImportView from '@/views/library/LibraryImportView.vue'
import { useLibraryImportStore } from '@/stores/libraryImport'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useConfigurationStore } from '@/stores/configuration'
import { apiService } from '@/services/api'
import type { RootFolder } from '@/types'

const folder = (id: number, isDefault = false) =>
  ({ id, name: `Folder ${id}`, path: `/srv/${id}`, isDefault }) as unknown as RootFolder

function mountView({
  realInit = false,
  keepPinia,
}: { realInit?: boolean; keepPinia?: Pinia } = {}) {
  const pinia = keepPinia ?? createPinia()
  setActivePinia(pinia)
  const rootFolders = useRootFoldersStore()
  rootFolders.folders = [folder(2, true), folder(3)]
  const config = useConfigurationStore()
  config.loadApplicationSettings = vi.fn().mockResolvedValue(undefined)
  const store = useLibraryImportStore()
  if (!realInit) store.initFromRootFolder = vi.fn().mockResolvedValue(undefined)
  store.triggerScan = vi.fn().mockResolvedValue(undefined)
  const wrapper = mount(LibraryImportView, {
    global: { plugins: [pinia], stubs: { RootFolderFormModal: true, LibraryImportFooter: true } },
  })
  return { wrapper, store }
}

describe('LibraryImportView opened from "Scan for unmatched files"', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('selects the requested folder, scans it once and drops the scan request from the address', async () => {
    routeQuery.value = { rootFolder: '3', scan: '1' }
    const { store } = mountView()
    await flushPromises()

    // Scanned directly: no load first, so changing the list meanwhile can't redirect the scan
    expect(store.initFromRootFolder).not.toHaveBeenCalled()
    expect(store.triggerScan).toHaveBeenCalledWith(3)
    expect(routerReplace).toHaveBeenCalledWith({ query: { rootFolder: '3', scan: undefined } })
  })

  it('locks the folder list and the Scan button while a scan runs', async () => {
    routeQuery.value = {}
    const { wrapper, store } = mountView()
    await flushPromises()

    store.scanStatus = 'scanning'
    await flushPromises()

    expect(wrapper.get('select.folder-select').attributes('disabled')).toBeDefined()
    expect(wrapper.get('button.btn-primary').attributes('disabled')).toBeDefined()
    await (wrapper.vm as unknown as { startScan: () => Promise<void> }).startScan()
    expect(store.triggerScan).not.toHaveBeenCalled()
  })

  it('shows the running scan when coming back to the page', async () => {
    routeQuery.value = {}
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryImportStore()
    store.rootFolderId = 3
    store.scanStatus = 'scanning'
    const { wrapper, store: sameStore } = mountView({ keepPinia: pinia })
    await flushPromises()

    expect(sameStore.initFromRootFolder).not.toHaveBeenCalled()
    expect((wrapper.get('select.folder-select').element as HTMLSelectElement).value).toBe('3')
    expect(wrapper.text()).toContain('Scanning for unmatched audio files')
  })

  it('shows the folder being matched when coming back to the page during Start Matching', async () => {
    routeQuery.value = {}
    const pinia = createPinia()
    setActivePinia(pinia)
    const store = useLibraryImportStore()
    store.rootFolderId = 3
    store.scanStatus = 'done'
    store.isProcessing = true
    const { wrapper, store: sameStore } = mountView({ keepPinia: pinia })
    await flushPromises()

    expect(sameStore.initFromRootFolder).not.toHaveBeenCalled()
    expect((wrapper.get('select.folder-select').element as HTMLSelectElement).value).toBe('3')
  })

  it('opens the default folder without scanning when nothing is requested', async () => {
    routeQuery.value = {}
    const { store } = mountView()
    await flushPromises()

    expect(store.initFromRootFolder).toHaveBeenCalledWith(2)
    expect(store.triggerScan).not.toHaveBeenCalled()
    expect(routerReplace).not.toHaveBeenCalled()
  })

  it('says a requested folder no longer exists, opens the default one and does not scan it', async () => {
    routeQuery.value = { rootFolder: '99', scan: '1' }
    const { wrapper, store } = mountView()
    await flushPromises()

    expect(store.initFromRootFolder).toHaveBeenCalledWith(2)
    expect(store.triggerScan).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="requested-folder-missing"]').text()).toContain(
      'This root folder no longer exists',
    )
    // Nothing is left to ask for: a reload opens the page normally
    expect(routerReplace).toHaveBeenCalledWith({
      query: { rootFolder: undefined, scan: undefined },
    })

    // Scanning the default folder clears the message
    await (wrapper.vm as unknown as { startScan: () => Promise<void> }).startScan()
    await flushPromises()
    expect(store.triggerScan).toHaveBeenCalledWith(2)
    expect(wrapper.find('[data-testid="requested-folder-missing"]').exists()).toBe(false)
  })

  it("keeps the missing-folder message above the default folder's saved results", async () => {
    routeQuery.value = { rootFolder: '99', scan: '1' }
    // The real store loads the default folder's saved results
    ;(apiService as unknown as Record<string, unknown>).getSavedUnmatchedFiles = vi
      .fn()
      .mockResolvedValue({
        items: [
          {
            fullPath: '/srv/2/Book.mp3',
            sourceFiles: ['/srv/2/Book.mp3'],
            bookFolder: '/srv/2',
            relativePath: 'Book',
            title: 'Book',
            author: null,
            series: null,
            asin: null,
            format: 'MP3',
            fileCount: 1,
          },
        ],
        lastScannedAt: '2026-10-19T10:00:00Z',
      })
    const { wrapper, store } = mountView({ realInit: true })
    await flushPromises()

    expect(store.scanStatus).toBe('done')
    expect(store.itemList).toHaveLength(1)
    expect(wrapper.find('[data-testid="requested-folder-missing"]').exists()).toBe(true)
    expect(store.triggerScan).not.toHaveBeenCalled()
    expect(routerReplace).toHaveBeenCalledWith({
      query: { rootFolder: undefined, scan: undefined },
    })
  })

  it('clears the missing-folder message when another folder is picked', async () => {
    routeQuery.value = { rootFolder: '99' }
    const { wrapper } = mountView()
    await flushPromises()
    expect(wrapper.find('[data-testid="requested-folder-missing"]').exists()).toBe(true)

    await wrapper.get('select.folder-select').setValue(3)
    await flushPromises()
    expect(wrapper.find('[data-testid="requested-folder-missing"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('No scan results yet')
  })
})
