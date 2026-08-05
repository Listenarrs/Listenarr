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
import { apiService } from '@/services/api'
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
    activeRelocation,
  }
}

describe('RootFoldersSettings', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    vi.clearAllMocks()
  })

  it('shows header spinner and loading state when store.loading is true', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

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

  it('reauthorizes the exact configured root path only after confirmation', async () => {
    const folder = rootFolder(null)
    vi.mocked(apiService.getRootFolders).mockResolvedValue([folder])
    vi.mocked(apiService.reauthorizeRootFolderIdentity).mockResolvedValue(folder)
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const action = wrapper.get('[data-cy="reauthorize-root-identity"]')
    await action.trigger('click')

    const displayedPath = wrapper.get('[data-testid="root-reauthorization-path"]')
    expect(displayedPath.element.textContent).toBe(folder.path)
    const confirm = wrapper.get('.modal-delete-button')
    expect(confirm.text()).toContain('Reauthorize root')
    await confirm.trigger('click')
    await flushPromises()

    expect(apiService.reauthorizeRootFolderIdentity).toHaveBeenCalledWith(folder.id, folder.path)
  })

  it('shows legacy reauthorization separately and confirms the exact target path', async () => {
    const legacy = relocation('LegacyUnenrolled')
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(legacy)])
    vi.mocked(apiService.reauthorizeLegacyRootFolderRelocationTarget).mockResolvedValue({
      ...legacy,
      status: 'Running',
      targetIdentityEnrollmentState: 'Authorized',
    })
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    const action = wrapper.get('[data-cy="reauthorize-relocation-target"]')
    expect(action.text()).toContain('Reauthorize target')
    expect(wrapper.findAll('button').some((button) => button.text().trim() === 'Retry')).toBe(false)

    await action.trigger('click')

    const displayedTarget = wrapper.get('[data-testid="reauthorization-target-path"]')
    expect(displayedTarget.element.textContent).toBe(targetPath)
    expect(displayedTarget.classes()).toContain('reauthorization-target-path')
    const confirm = wrapper.get('.modal-delete-button')
    expect(confirm.text()).toContain('Reauthorize target')
    await confirm.trigger('click')
    await flushPromises()

    expect(apiService.reauthorizeLegacyRootFolderRelocationTarget).toHaveBeenCalledWith(
      'relocation-1',
      targetPath,
    )
    expect(wrapper.emitted('close')).toBeUndefined()
  })

  it('keeps ordinary retry separate for an authorized relocation', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(relocation('Authorized'))])
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.find('[data-cy="reauthorize-relocation-target"]').exists()).toBe(false)
    expect(wrapper.findAll('button').some((button) => button.text().trim() === 'Retry')).toBe(true)
  })

  it('fails closed when the target identity is unavailable', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(relocation('Unavailable'))])
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.find('[data-cy="reauthorize-relocation-target"]').exists()).toBe(false)
    expect(wrapper.findAll('button').some((button) => button.text().trim() === 'Retry')).toBe(false)
  })
})
