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
import { createPinia, setActivePinia } from 'pinia'

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
import type { RootFolder } from '@/types'

const folder = (id: number, isDefault = false) =>
  ({ id, name: `Folder ${id}`, path: `/srv/${id}`, isDefault }) as unknown as RootFolder

function mountView() {
  const pinia = createPinia()
  setActivePinia(pinia)
  const rootFolders = useRootFoldersStore()
  rootFolders.folders = [folder(2, true), folder(3)]
  const config = useConfigurationStore()
  config.loadApplicationSettings = vi.fn().mockResolvedValue(undefined)
  const store = useLibraryImportStore()
  store.initFromRootFolder = vi.fn().mockResolvedValue(undefined)
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

    expect(store.initFromRootFolder).toHaveBeenCalledWith(3)
    expect(store.triggerScan).toHaveBeenCalledWith(3)
    expect(routerReplace).toHaveBeenCalledWith({ query: { rootFolder: '3', scan: undefined } })
  })

  it('opens the default folder without scanning when nothing is requested', async () => {
    routeQuery.value = {}
    const { store } = mountView()
    await flushPromises()

    expect(store.initFromRootFolder).toHaveBeenCalledWith(2)
    expect(store.triggerScan).not.toHaveBeenCalled()
    expect(routerReplace).not.toHaveBeenCalled()
  })

  it('does not scan a folder that does not exist', async () => {
    routeQuery.value = { rootFolder: '99', scan: '1' }
    const { store } = mountView()
    await flushPromises()

    expect(store.initFromRootFolder).toHaveBeenCalledWith(2)
    expect(store.triggerScan).not.toHaveBeenCalled()
  })
})
