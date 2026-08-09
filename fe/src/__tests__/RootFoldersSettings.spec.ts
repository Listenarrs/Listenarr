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
import { signalRService } from '@/services/signalr'
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
    storageState: 'Healthy',
    storageReason: 'None',
    canConfirmCurrentFolder: false,
    canChangePath: true,
    canMutateFilesystem: true,
    activeRelocation,
  }
}

describe('RootFoldersSettings', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    vi.clearAllMocks()
    vi.mocked(apiService.getRootFolders).mockReset().mockResolvedValue([])
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
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.text()).toContain('NeedsAttention')
    connected?.()
    await flushPromises()

    expect(apiService.getRootFolders).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).not.toContain('NeedsAttention')
    wrapper.unmount()
    expect(unsubscribe).toHaveBeenCalledTimes(1)
  })

  it.each([
    ['Healthy', 'Healthy', true, false],
    ['Missing', 'Missing', false, false],
    ['Unavailable', 'Unavailable', false, false],
    ['Unconfirmed', 'Needs confirmation', false, true],
  ] as const)(
    'renders %s storage state with the correct actions',
    async (storageState, label, canMutateFilesystem, canConfirmCurrentFolder) => {
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
                : ('AccessDenied' as const),
        storageMessage:
          storageState === 'Healthy' ? null : `Storage is ${storageState.toLowerCase()}.`,
        canMutateFilesystem,
        canConfirmCurrentFolder,
        confirmationToken: canConfirmCurrentFolder ? 'observation-token' : null,
      }
      vi.mocked(apiService.getRootFolders).mockResolvedValue([folder])
      const pinia = createPinia()
      setActivePinia(pinia)
      const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
      await flushPromises()

      expect(wrapper.text()).toContain(label)
      expect(wrapper.get('[data-cy="scan-unmatched"]').attributes('disabled') !== undefined).toBe(
        !canMutateFilesystem,
      )
      expect(wrapper.find('[data-cy="confirm-root-folder"]').exists()).toBe(canConfirmCurrentFolder)
      wrapper.unmount()
    },
  )

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
    const pinia = createPinia()
    setActivePinia(pinia)
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

  it('keeps ordinary retry separate for an authorized relocation', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(relocation('Authorized'))])
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.find('[data-cy="confirm-root-folder"]').exists()).toBe(false)
    expect(wrapper.findAll('button').some((button) => button.text().trim() === 'Retry')).toBe(true)
  })

  it('fails closed when the target identity is unavailable', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValue([rootFolder(relocation('Unavailable'))])
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(RootFoldersSettings, { global: { plugins: [pinia] } })
    await flushPromises()

    expect(wrapper.find('[data-cy="confirm-root-folder"]').exists()).toBe(false)
    expect(wrapper.findAll('button').some((button) => button.text().trim() === 'Retry')).toBe(false)
  })
})
