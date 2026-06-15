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
import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import RootFoldersSettings from '@/components/settings/RootFoldersSettings.vue'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { createDeferred, flushAsync } from '@/test/utils/wait'

describe('RootFoldersSettings', () => {
  it('shows header spinner and loading state when store.loading is true', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    useRootFoldersStore()

    // Make the underlying API call pending so store.loading remains true while mounted
    const api = await import('@/services/api')
    const pendingFolders = createDeferred<unknown[]>()
    // spy on the apiService instance method (module-level named export is not present in TS types)
    vi.spyOn((api as any).apiService, 'getRootFolders').mockImplementation(
      () => pendingFolders.promise as any,
    )

    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    // Wait for onMounted to run and for store.load() to set loading=true
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.loading-state').exists()).toBe(true)
    expect(wrapper.find('.section-header .small-inline-spinner').exists()).toBe(true)

    // Resolve API and ensure UI updates
    pendingFolders.resolve([])
    await flushAsync()
  })
})
