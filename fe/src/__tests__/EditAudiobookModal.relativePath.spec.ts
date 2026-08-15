/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createPinia } from 'pinia'

const toastMocks = vi.hoisted(() => ({
  error: vi.fn(),
  info: vi.fn(),
  success: vi.fn(),
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => toastMocks,
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getAudiobook: vi.fn().mockImplementation(async (id: number) => ({ id })),
    getQualityProfiles: vi.fn().mockResolvedValue([]),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
    getAudiobookIdentifiers: vi.fn().mockResolvedValue({ identifiers: [] }),
    getRootFolders: vi.fn(),
  },
}))

import { apiService } from '@/services/api'
import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'

const defaultRoot = {
  id: 1,
  name: 'Default',
  path: 'C:\\root',
  isDefault: true,
  resolvedCaseSensitivity: 'Insensitive' as const,
}

const audiobook = {
  id: 1,
  title: 'Sample',
  authors: ['Author'],
  basePath: 'C:\\root\\Some Author\\Some Title',
  monitored: true,
  tags: [],
}

type EditDestinationVm = {
  selectedRootId: number | null
  unmanagedExistingDestination: boolean
  editingDestination: boolean
  formData: { relativePath: string | null }
  combinedBasePath: () => string | null
  startEditingDestination: () => void
  finishEditingDestination: () => void
}

async function mountModal(
  candidate = audiobook,
): Promise<{ wrapper: VueWrapper; vm: EditDestinationVm }> {
  const wrapper = mount(EditAudiobookModal, {
    props: {
      isOpen: true,
      audiobook: candidate,
    },
    attachTo: document.body,
    global: {
      plugins: [createPinia()],
    },
  })

  await new Promise((resolve) => setTimeout(resolve, 25))
  return {
    wrapper,
    vm: wrapper.vm as unknown as EditDestinationVm,
  }
}

describe('EditAudiobookModal configured-root destination editing', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(apiService.getRootFolders).mockResolvedValue([defaultRoot])
  })

  it('shows the full stored path while deriving a configured-root relative path', async () => {
    const { wrapper, vm } = await mountModal()

    expect((vm.combinedBasePath() || '').replace(/\\/g, '/')).toBe('C:/root/Some Author/Some Title')
    expect(vm.formData.relativePath).toBe('Some Author\\Some Title')
    expect(
      (wrapper.get('.readonly-input').element as HTMLInputElement).value.replace(/\\/g, '/'),
    ).toBe('C:/root/Some Author/Some Title')
  })

  it('treats an exact configured root as the selected root with an empty relative path', async () => {
    const { vm } = await mountModal({
      ...audiobook,
      basePath: 'C:\\root',
    })

    expect(vm.selectedRootId).toBe(1)
    expect(vm.formData.relativePath).toBe('')
  })

  it('selects the most specific configured root for nested roots', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([
      defaultRoot,
      {
        id: 2,
        name: 'Nested sensitive root',
        path: 'C:\\root\\Sensitive',
        isDefault: false,
        resolvedCaseSensitivity: 'Sensitive',
      },
    ])

    const { vm } = await mountModal({
      ...audiobook,
      basePath: 'C:\\root\\Sensitive\\Book',
    })

    expect(vm.selectedRootId).toBe(2)
    expect(vm.formData.relativePath).toBe('Book')
  })

  it('rejects an absolute destination even when it is inside the selected root', async () => {
    const { vm } = await mountModal()

    vm.startEditingDestination()
    vm.formData.relativePath = 'C:\\root\\New Author\\New Title'
    vm.finishEditingDestination()

    expect(vm.formData.relativePath).toBe('C:\\root\\New Author\\New Title')
    expect(vm.editingDestination).toBe(true)
    expect(toastMocks.error).toHaveBeenCalledWith(
      'Invalid destination',
      'Enter a path relative to the selected configured root folder.',
    )
  })

  it('rejects a Windows root-relative destination', async () => {
    const { vm } = await mountModal()

    vm.startEditingDestination()
    vm.formData.relativePath = '\\Redirected Title'
    vm.finishEditingDestination()

    expect(vm.formData.relativePath).toBe('\\Redirected Title')
    expect(vm.editingDestination).toBe(true)
    expect(toastMocks.error).toHaveBeenCalledWith(
      'Invalid destination',
      'Enter a path relative to the selected configured root folder.',
    )
  })

  it('does not expose an arbitrary custom-path destination mode', async () => {
    const { wrapper } = await mountModal()

    await wrapper.get('button[aria-label="Edit destination"]').trigger('click')
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).not.toContain('Custom path')
    expect(wrapper.find('.custom-input').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="Browse for folder"]').exists()).toBe(false)
  })

  it('keeps a legacy out-of-root path visible until a configured-root relative path is chosen', async () => {
    const legacyPath = 'D:\\legacy\\Author\\Title'
    const { wrapper, vm } = await mountModal({
      ...audiobook,
      basePath: legacyPath,
    })

    expect(vm.unmanagedExistingDestination).toBe(true)
    expect((wrapper.get('.readonly-input').element as HTMLInputElement).value).toBe(legacyPath)

    vm.startEditingDestination()
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain(
      'Enter a path relative to the selected configured root folder.',
    )

    vm.formData.relativePath = 'Author\\Title'
    vm.finishEditingDestination()

    expect(vm.unmanagedExistingDestination).toBe(false)
    expect((vm.combinedBasePath() || '').replace(/\\/g, '/')).toBe('C:/root/Author/Title')
    expect(vm.editingDestination).toBe(false)
  })

  it('uses Unix separators when the configured Unix root contains a literal backslash', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([
      {
        id: 9,
        name: 'Unix root with backslash',
        path: '/library/Books\\Archive',
        pathSyntax: 'Unix',
        isDefault: true,
        resolvedCaseSensitivity: 'Sensitive',
      },
    ])
    vi.mocked(apiService.getApplicationSettings).mockResolvedValueOnce({
      outputPath: '/library/Books\\Archive',
    })

    const { vm } = await mountModal({
      ...audiobook,
      basePath: '/library/Books\\Archive/Author/Title',
    })

    vm.startEditingDestination()
    vm.formData.relativePath = 'Other/Book'
    vm.finishEditingDestination()

    expect(vm.combinedBasePath()).toBe('/library/Books\\Archive/Other/Book')
    expect(vm.editingDestination).toBe(false)
  })

  it('treats a leading backslash as relative under an explicit Unix root', async () => {
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([
      {
        id: 8,
        name: 'Unix root',
        path: '/library',
        pathSyntax: 'Unix',
        isDefault: true,
        resolvedCaseSensitivity: 'Sensitive',
      },
    ])
    vi.mocked(apiService.getApplicationSettings).mockResolvedValueOnce({ outputPath: '/library' })

    const { vm } = await mountModal({
      ...audiobook,
      basePath: '/library/Author/Title',
    })

    vm.startEditingDestination()
    vm.formData.relativePath = '\\Chapter'
    vm.finishEditingDestination()

    expect(vm.formData.relativePath).toBe('\\Chapter')
    expect(vm.editingDestination).toBe(false)
  })
})
