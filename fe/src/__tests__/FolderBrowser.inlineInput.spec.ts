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
import { vi, describe, it, expect } from 'vitest'

// Avoid network calls from browse/validate during interaction.
vi.mock('@/services/api', () => ({
  apiService: {
    browseDirectory: vi.fn().mockResolvedValue({ currentPath: '', parentPath: null, items: [] }),
    validatePath: vi.fn().mockResolvedValue({ isValid: true, message: 'Valid' }),
  },
}))

import FolderBrowser from '@/components/ui/FolderBrowser.vue'

describe('FolderBrowser inline input (issue #523)', () => {
  it('commits a manually-typed path via update:modelValue when autoSelect is on', async () => {
    const wrapper = mount(FolderBrowser, {
      props: { inline: true },
    })

    const input = wrapper.find('input.browser-input')
    expect(input.exists()).toBe(true)

    await input.setValue('/mnt/media/audiobooks')

    const emitted = wrapper.emitted('update:modelValue')
    expect(emitted).toBeTruthy()
    expect(emitted?.at(-1)).toEqual(['/mnt/media/audiobooks'])
  })

  it('stays silent on update:modelValue when autoSelect is false (draft only)', async () => {
    const wrapper = mount(FolderBrowser, {
      props: { inline: true, autoSelect: false },
    })

    const input = wrapper.find('input.browser-input')
    expect(input.exists()).toBe(true)

    await input.setValue('/mnt/media/audiobooks')

    // No finalized selection...
    expect(wrapper.emitted('update:modelValue')).toBeFalsy()
    // ...but the typed value still surfaces as a draft for modal consumers.
    const draft = wrapper.emitted('path-draft')
    expect(draft).toBeTruthy()
    expect(draft?.at(-1)).toEqual(['/mnt/media/audiobooks'])
  })
})
