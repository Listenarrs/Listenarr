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
import { vi, describe, it, expect, beforeEach } from 'vitest'

vi.mock('@/services/api', () => ({
  apiService: {
    getAudibleMetadata: vi.fn().mockResolvedValue({}),
    previewLibraryPath: vi
      .fn()
      .mockResolvedValue({ fullPath: '/root/Author/Title', relativePath: 'Author/Title' }),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: '/root' }),
    getQualityProfiles: vi.fn(),
    getRootFolders: vi.fn().mockResolvedValue([]),
    addToLibrary: vi.fn().mockResolvedValue({ audiobook: { id: 1 } }),
  },
}))

import AddLibraryModal from '@/components/domain/audiobook/AddLibraryModal.vue'

const fakeBook = {
  title: 'Test Title',
  authors: ['Author One'],
  imageUrl: '',
}

async function mountModal() {
  const wrapper = mount(AddLibraryModal, {
    props: { visible: false, book: fakeBook },
    attachTo: document.body,
    global: {
      plugins: [(await import('pinia')).createPinia()],
      stubs: {
        RouterLink: { props: ['to'], template: '<a class="router-link-stub"><slot /></a>' },
      },
    },
  })
  await wrapper.setProps({ visible: true })
  await new Promise((resolve) => setTimeout(resolve, 10))
  await wrapper.vm.$nextTick()
  return wrapper
}

function addButton() {
  const buttons = Array.from(document.body.querySelectorAll('button'))
  return buttons.find((button) => button.textContent?.includes('Add to Library')) as
    | HTMLButtonElement
    | undefined
}

describe('AddLibraryModal quality profile requirement', () => {
  beforeEach(async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.addToLibrary).mockClear()
  })

  it('preselects the default profile and submits it', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getQualityProfiles).mockResolvedValue([
      { id: 4, name: 'Lossless', isDefault: false },
      { id: 7, name: 'Standard', isDefault: true },
    ] as never)
    const wrapper = await mountModal()

    const select = document.body.querySelector(
      '[data-testid="quality-profile-select"]',
    ) as HTMLSelectElement
    expect(select.value).toBe('7')
    expect(document.body.querySelector('[data-testid="quality-profile-warning"]')).toBeNull()

    await (wrapper.vm as unknown as { addToLibrary: () => Promise<void> }).addToLibrary()
    expect(apiService.addToLibrary).toHaveBeenCalledWith(
      expect.any(Object),
      expect.objectContaining({ qualityProfileId: 7 }),
    )
    wrapper.unmount()
  })

  it('warns with a settings link and blocks adding when no profiles exist', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getQualityProfiles).mockResolvedValue([])
    const wrapper = await mountModal()

    const warning = document.body.querySelector('[data-testid="quality-profile-warning"]')
    expect(warning?.textContent).toContain('No quality profiles exist')
    expect(warning?.querySelector('.router-link-stub')?.textContent).toContain(
      'Settings → Quality Profiles',
    )
    expect(addButton()?.disabled).toBe(true)

    await (wrapper.vm as unknown as { addToLibrary: () => Promise<void> }).addToLibrary()
    expect(apiService.addToLibrary).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('requires an explicit choice when profiles exist but none is the default', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getQualityProfiles).mockResolvedValue([
      { id: 4, name: 'Lossless', isDefault: false },
    ] as never)
    const wrapper = await mountModal()

    expect(
      document.body.querySelector('[data-testid="quality-profile-error"]')?.textContent,
    ).toContain('Select a quality profile')
    expect(addButton()?.disabled).toBe(true)
    await (wrapper.vm as unknown as { addToLibrary: () => Promise<void> }).addToLibrary()
    expect(apiService.addToLibrary).not.toHaveBeenCalled()

    const select = document.body.querySelector(
      '[data-testid="quality-profile-select"]',
    ) as HTMLSelectElement
    select.value = '4'
    select.dispatchEvent(new Event('change'))
    await wrapper.vm.$nextTick()
    expect(document.body.querySelector('[data-testid="quality-profile-error"]')).toBeNull()

    await (wrapper.vm as unknown as { addToLibrary: () => Promise<void> }).addToLibrary()
    expect(apiService.addToLibrary).toHaveBeenCalledWith(
      expect.any(Object),
      expect.objectContaining({ qualityProfileId: 4 }),
    )
    wrapper.unmount()
  })
})
