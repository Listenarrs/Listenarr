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
import { mount, flushPromises } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'
import { describe, it, beforeEach, expect, vi } from 'vitest'
import WantedView from '@/views/content/WantedView.vue'
import AssignQualityProfileModal from '@/components/domain/audiobook/AssignQualityProfileModal.vue'
import { useLibraryStore } from '@/stores/library'
import { useConfigurationStore } from '@/stores/configuration'
import { apiService } from '@/services/api'
import type { QualityProfile } from '@/types'

const toastSuccess = vi.fn()
const toastWarning = vi.fn()
const toastError = vi.fn()

vi.mock('@/services/toastService', () => ({
  useToast: () => ({
    success: toastSuccess,
    warning: toastWarning,
    error: toastError,
    info: vi.fn(),
  }),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getImageUrl: vi.fn((url: string) => url || ''),
    getLibrary: vi.fn(async () => []),
    getQualityProfiles: vi.fn(async () => []),
    getDownloads: vi.fn(async () => []),
    searchAndDownload: vi.fn(async () => ({ success: true, indexerUsed: 'MockIndexer' })),
    bulkUpdateAudiobooks: vi.fn(async (ids: number[]) => ({
      message: 'ok',
      results: ids.map((id) => ({ id, success: true, errors: [] as string[] })),
    })),
  },
  getImageUrl: vi.fn((url: string) => url || ''),
  ensureImageCached: vi.fn(async () => true),
}))

const standardProfile: QualityProfile = { id: 7, name: 'Standard', qualities: [], isDefault: true }
const loslessProfile: QualityProfile = { id: 9, name: 'Lossless', qualities: [], isDefault: false }

// The modal pieces are stubbed globally by test-setup.ts (inline, no teleport); the
// stubbed ModalHeader exposes a `.close-btn` that emits `close`, which is the cancel path.
const stubs = {
  RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
  ManualSearchModal: true,
  ManualImportModal: true,
}

describe('WantedView - quality profile recovery', () => {
  let pinia: ReturnType<typeof createPinia>
  let libraryStore: ReturnType<typeof useLibraryStore>
  let configurationStore: ReturnType<typeof useConfigurationStore>
  // The real store action: fetchLibrary() swallows apiService.getLibrary() failures (it records
  // libraryStore.error instead of throwing), which the refresh-failure cases must exercise.
  let realFetchLibrary: ReturnType<typeof useLibraryStore>['fetchLibrary']

  const seedWanted = () => {
    libraryStore.audiobooks = [
      { id: 1, title: 'No Profile Book', monitored: true, files: [] },
      { id: 2, title: 'Dangling Profile Book', monitored: true, files: [], qualityProfileId: 404 },
      { id: 3, title: 'Already Assigned', monitored: true, files: [], qualityProfileId: 7 },
    ] as unknown as ReturnType<typeof useLibraryStore>['audiobooks']
  }

  const mountView = async () => {
    const wrapper = mount(WantedView, { global: { plugins: [pinia], stubs } })
    await flushPromises()
    return wrapper
  }

  beforeEach(() => {
    pinia = createPinia()
    setActivePinia(pinia)
    libraryStore = useLibraryStore()
    configurationStore = useConfigurationStore()
    realFetchLibrary = libraryStore.fetchLibrary
    libraryStore.fetchLibrary = vi.fn(async () => undefined)
    configurationStore.loadQualityProfiles = vi.fn(async () => undefined)
    vi.clearAllMocks()
    vi.stubGlobal(
      'matchMedia',
      vi.fn().mockImplementation(() => ({
        matches: false,
        media: '',
        onchange: null,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    )
  })

  it('shows "No profile" per row and a warning that links to Settings when no profiles exist', async () => {
    seedWanted()
    configurationStore.qualityProfiles = []

    const wrapper = await mountView()

    const warning = wrapper.find('[data-testid="profile-warning"]')
    expect(warning.exists()).toBe(true)
    // Every wanted book is affected: none can resolve a profile when there are none.
    expect(warning.text()).toContain('3 wanted audiobooks have no quality profile')
    expect(warning.text()).toContain('Automatic search skips')
    const link = warning.find('a')
    expect(link.attributes('href')).toBe('/settings#quality-profiles')
    expect(link.text()).toContain('Quality Profiles')
    // Nothing to assign yet, so no assign action is offered.
    expect(wrapper.find('[data-testid="assign-profile-open"]').exists()).toBe(false)
    expect(wrapper.findAll('.quality-tag-missing')).toHaveLength(3)
  })

  it('opens the picker with nothing preselected when no profile is marked default', async () => {
    seedWanted()
    configurationStore.qualityProfiles = [{ ...standardProfile, isDefault: false }, loslessProfile]

    const wrapper = await mountView()
    expect(wrapper.find('[data-testid="profile-warning"]').text()).toContain(
      '2 wanted audiobooks have no quality profile',
    )

    await wrapper.find('[data-testid="assign-profile-open"]').trigger('click')
    await flushPromises()

    const modal = wrapper.findComponent(AssignQualityProfileModal)
    expect(modal.props('visible')).toBe(true)
    expect(modal.props('audiobooks').map((a: { id: number }) => a.id)).toEqual([1, 2])

    const select = modal.find('select')
    expect((select.element as HTMLSelectElement).selectedIndex).toBe(0)
    expect(modal.text()).toContain('No profile is marked as default')
    expect(modal.find('[data-testid="assign-confirm"]').attributes('disabled')).toBeDefined()

    // Any valid profile can be chosen even without a default.
    await select.setValue(9)
    expect(modal.find('[data-testid="assign-confirm"]').attributes('disabled')).toBeUndefined()
  })

  it('preselects the default profile and assigns it only to books without a valid profile', async () => {
    seedWanted()
    configurationStore.qualityProfiles = [standardProfile, loslessProfile]

    const wrapper = await mountView()
    await wrapper.find('[data-testid="assign-profile-open"]').trigger('click')
    await flushPromises()

    const modal = wrapper.findComponent(AssignQualityProfileModal)
    expect((modal.find('select').element as HTMLSelectElement).value).toBe('7')

    await modal.find('[data-testid="assign-confirm"]').trigger('click')
    await flushPromises()

    // Book 3 already has a valid profile and must not be touched.
    expect(apiService.bulkUpdateAudiobooks).toHaveBeenCalledTimes(1)
    expect(apiService.bulkUpdateAudiobooks).toHaveBeenCalledWith([1, 2], { qualityProfileId: 7 })
    expect(libraryStore.fetchLibrary).toHaveBeenCalled()
    expect(toastSuccess).toHaveBeenCalledWith(
      'Quality profile assigned',
      expect.stringContaining('"Standard" to 2 wanted audiobooks'),
    )
    expect(modal.props('visible')).toBe(false)
  })

  it('cancelling the picker changes nothing', async () => {
    seedWanted()
    configurationStore.qualityProfiles = [standardProfile]

    const wrapper = await mountView()
    await wrapper.find('[data-testid="assign-profile-open"]').trigger('click')
    await flushPromises()

    const modal = wrapper.findComponent(AssignQualityProfileModal)
    expect(modal.props('visible')).toBe(true)

    await modal.find('.close-btn').trigger('click')
    await flushPromises()

    expect(modal.props('visible')).toBe(false)
    expect(apiService.bulkUpdateAudiobooks).not.toHaveBeenCalled()
    expect(libraryStore.fetchLibrary).not.toHaveBeenCalled()
    expect(toastSuccess).not.toHaveBeenCalled()
    expect(toastWarning).not.toHaveBeenCalled()
  })

  it('keeps the picker open and names the books that failed on a partial failure', async () => {
    seedWanted()
    configurationStore.qualityProfiles = [standardProfile]
    vi.mocked(apiService.bulkUpdateAudiobooks).mockResolvedValueOnce({
      message: 'partial',
      results: [
        { id: 1, success: true, errors: [] },
        { id: 2, success: false, errors: ['Invalid qualityProfileId value'] },
      ],
    })

    const wrapper = await mountView()
    await wrapper.find('[data-testid="assign-profile-open"]').trigger('click')
    await flushPromises()

    const modal = wrapper.findComponent(AssignQualityProfileModal)
    await modal.find('[data-testid="assign-confirm"]').trigger('click')
    await flushPromises()

    expect(modal.props('visible')).toBe(true)
    const failures = modal.find('[data-testid="assign-failures"]')
    expect(failures.exists()).toBe(true)
    expect(failures.text()).toContain('1 of 2 could not be updated')
    expect(failures.text()).toContain('Dangling Profile Book')
    expect(failures.text()).toContain('Invalid qualityProfileId value')
    expect(failures.text()).not.toContain('No Profile Book')
    expect(libraryStore.fetchLibrary).toHaveBeenCalled()
    expect(toastWarning).toHaveBeenCalledWith(
      'Assignment incomplete',
      expect.stringContaining('1 of 2'),
    )
    expect(toastSuccess).not.toHaveBeenCalled()
  })

  it('keeps the picker and assign action disabled until the library refresh settles', async () => {
    seedWanted()
    configurationStore.qualityProfiles = [standardProfile, loslessProfile]
    let finishRefresh!: () => void
    libraryStore.fetchLibrary = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          finishRefresh = resolve
        }),
    )

    const wrapper = await mountView()
    await wrapper.find('[data-testid="assign-profile-open"]').trigger('click')
    await flushPromises()

    const modal = wrapper.findComponent(AssignQualityProfileModal)
    await modal.find('[data-testid="assign-confirm"]').trigger('click')
    await flushPromises()

    // The update has landed but the refresh is still pending: the old target list is stale,
    // so nothing may be (re)submitted against it.
    expect(apiService.bulkUpdateAudiobooks).toHaveBeenCalledTimes(1)
    expect(libraryStore.fetchLibrary).toHaveBeenCalledTimes(1)
    expect(modal.props('visible')).toBe(true)
    expect(modal.find('select').attributes('disabled')).toBeDefined()
    const confirm = modal.find('[data-testid="assign-confirm"]')
    expect(confirm.attributes('disabled')).toBeDefined()
    expect(confirm.text()).toContain('Assigning…')
    await confirm.trigger('click')
    await flushPromises()
    expect(apiService.bulkUpdateAudiobooks).toHaveBeenCalledTimes(1)
    expect(toastSuccess).not.toHaveBeenCalled()

    finishRefresh()
    await flushPromises()

    expect(toastSuccess).toHaveBeenCalledWith('Quality profile assigned', expect.any(String))
    expect(modal.props('visible')).toBe(false)
  })

  it('retries only the books that failed, even when the refresh after a partial success fails', async () => {
    seedWanted()
    configurationStore.qualityProfiles = [standardProfile]
    vi.mocked(apiService.bulkUpdateAudiobooks).mockResolvedValueOnce({
      message: 'partial',
      results: [
        { id: 1, success: true, errors: [] },
        { id: 2, success: false, errors: ['Invalid qualityProfileId value'] },
      ],
    })
    libraryStore.fetchLibrary = realFetchLibrary
    vi.mocked(apiService.getLibrary).mockRejectedValue(new Error('offline'))

    const wrapper = await mountView()
    await wrapper.find('[data-testid="assign-profile-open"]').trigger('click')
    await flushPromises()

    const modal = wrapper.findComponent(AssignQualityProfileModal)
    expect(modal.props('audiobooks').map((a: { id: number }) => a.id)).toEqual([1, 2])
    await modal.find('[data-testid="assign-confirm"]').trigger('click')
    await flushPromises()

    // Book 1 succeeded but the list could not reload (the store swallows the failure), so its
    // row would still look unassigned. It must leave the target list anyway; only book 2
    // remains to retry.
    expect(apiService.getLibrary).toHaveBeenCalledTimes(1)
    expect(libraryStore.error).toBe('offline')
    expect(modal.props('visible')).toBe(true)
    expect(toastWarning).toHaveBeenCalledWith('Library refresh failed', expect.any(String))
    expect(toastWarning).toHaveBeenCalledWith(
      'Assignment incomplete',
      expect.stringContaining('1 of 2'),
    )
    expect(modal.props('audiobooks').map((a: { id: number }) => a.id)).toEqual([2])
    expect(modal.text()).toContain('1 wanted audiobook has no quality profile')
    expect(modal.find('[data-testid="assign-failures"]').text()).toContain('Dangling Profile Book')

    const confirm = modal.find('[data-testid="assign-confirm"]')
    expect(confirm.attributes('disabled')).toBeUndefined()
    expect(confirm.text()).toContain('Assign to 1')
    await confirm.trigger('click')
    await flushPromises()

    expect(apiService.bulkUpdateAudiobooks).toHaveBeenCalledTimes(2)
    expect(apiService.bulkUpdateAudiobooks).toHaveBeenLastCalledWith([2], { qualityProfileId: 7 })
  })

  it('keeps an assigned book out of the picker across close and reopen when the refresh failed', async () => {
    seedWanted()
    configurationStore.qualityProfiles = [standardProfile, loslessProfile]
    vi.mocked(apiService.bulkUpdateAudiobooks).mockResolvedValueOnce({
      message: 'partial',
      results: [
        { id: 1, success: true, errors: [] },
        { id: 2, success: false, errors: ['Invalid qualityProfileId value'] },
      ],
    })
    libraryStore.fetchLibrary = realFetchLibrary
    vi.mocked(apiService.getLibrary).mockRejectedValue(new Error('offline'))

    const wrapper = await mountView()
    await wrapper.find('[data-testid="assign-profile-open"]').trigger('click')
    await flushPromises()

    // Standard lands on book 1; the reload fails, so the server rows never arrive.
    const modal = wrapper.findComponent(AssignQualityProfileModal)
    await modal.find('[data-testid="assign-confirm"]').trigger('click')
    await flushPromises()
    expect(apiService.bulkUpdateAudiobooks).toHaveBeenLastCalledWith([1, 2], {
      qualityProfileId: 7,
    })
    expect(libraryStore.error).toBe('offline')
    expect(toastWarning).toHaveBeenCalledWith('Library refresh failed', expect.any(String))

    // Close the picker (which resets its own session memory) and reopen it.
    await modal.find('.close-btn').trigger('click')
    await flushPromises()
    expect(modal.props('visible')).toBe(false)
    expect(wrapper.find('[data-testid="profile-warning"]').text()).toContain(
      '1 wanted audiobook has no quality profile',
    )
    await wrapper.find('[data-testid="assign-profile-open"]').trigger('click')
    await flushPromises()

    // Book 1 already has Standard: it must not be offered again, so choosing Lossless now
    // cannot overwrite it.
    expect(modal.props('visible')).toBe(true)
    expect(modal.props('audiobooks').map((a: { id: number }) => a.id)).toEqual([2])
    await modal.find('select').setValue(9)
    await modal.find('[data-testid="assign-confirm"]').trigger('click')
    await flushPromises()

    expect(apiService.bulkUpdateAudiobooks).toHaveBeenCalledTimes(2)
    expect(apiService.bulkUpdateAudiobooks).toHaveBeenLastCalledWith([2], { qualityProfileId: 9 })
    expect(libraryStore.audiobooks.find((book) => book.id === 1)?.qualityProfileId).toBe(7)
    expect(libraryStore.audiobooks.find((book) => book.id === 2)?.qualityProfileId).toBe(9)
    expect(toastSuccess).toHaveBeenCalledWith('Quality profile assigned', expect.any(String))
    expect(modal.props('visible')).toBe(false)
  })

  it('reports every book as failed when the request itself fails', async () => {
    seedWanted()
    configurationStore.qualityProfiles = [standardProfile]
    vi.mocked(apiService.bulkUpdateAudiobooks).mockRejectedValueOnce(new Error('boom'))

    const wrapper = await mountView()
    await wrapper.find('[data-testid="assign-profile-open"]').trigger('click')
    await flushPromises()

    const modal = wrapper.findComponent(AssignQualityProfileModal)
    await modal.find('[data-testid="assign-confirm"]').trigger('click')
    await flushPromises()

    expect(modal.props('visible')).toBe(true)
    const failures = modal.find('[data-testid="assign-failures"]')
    expect(failures.text()).toContain('2 of 2 could not be updated')
    expect(toastWarning).toHaveBeenCalledWith(
      'Assignment incomplete',
      expect.stringContaining('0 of 2'),
    )
  })
})
