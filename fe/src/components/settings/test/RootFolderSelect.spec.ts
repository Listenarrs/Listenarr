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
import { mount } from '@vue/test-utils'
import { createPinia } from 'pinia'

vi.mock('@/services/api', () => ({
  apiService: {
    getRootFolders: vi.fn(),
  },
}))

import { apiService } from '@/services/api'
import RootFolderSelect from '@/components/form/RootFolderSelect.vue'

describe('RootFolderSelect', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(apiService.getRootFolders).mockResolvedValue([
      { id: 1, name: 'Primary', path: '/library', isDefault: true },
      { id: 2, name: 'Archive', path: '/archive', isDefault: false },
    ])
  })

  it('offers only the default and configured roots', async () => {
    const wrapper = mount(RootFolderSelect, {
      props: { rootId: null },
      global: { plugins: [createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 20))

    expect(wrapper.findAll('option').map((option) => option.text())).toEqual([
      'Use default',
      'Primary — /library',
      'Archive — /archive',
    ])
    expect(wrapper.text()).not.toContain('Custom path')
  })

  it('emits only configured root IDs or the default selection', async () => {
    const wrapper = mount(RootFolderSelect, {
      props: { rootId: null },
      global: { plugins: [createPinia()] },
    })

    await new Promise((resolve) => setTimeout(resolve, 20))
    const select = wrapper.get('select')

    await select.setValue('2')
    expect(wrapper.emitted('update:rootId')?.at(-1)).toEqual([2])

    await select.setValue('__null__')
    expect(wrapper.emitted('update:rootId')?.at(-1)).toEqual([null])
    expect(wrapper.emitted('update:customPath')).toBeUndefined()
  })
})
