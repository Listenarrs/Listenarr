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
import { modalStubs } from '@/test/stubs'

// Mock apiService methods used during mount/seedPreview to avoid network calls
vi.mock('@/services/api', () => ({
  apiService: {
    getAudibleMetadata: vi.fn().mockResolvedValue({}),
    previewLibraryPath: vi
      .fn()
      .mockResolvedValue({ fullPath: 'C:\\root\\Author\\Title', relativePath: '' }),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
    getQualityProfiles: vi.fn().mockResolvedValue([]),
    getRootFolders: vi.fn().mockResolvedValue([]),
  },
}))

import AddLibraryModal from '@/components/domain/audiobook/AddLibraryModal.vue'
import { delay } from '@/test/utils/wait'

const fakeBook = {
  title: 'Test Title',
  authors: ['Author One'],
  imageUrl: '',
  asin: 'B001234567',
}

describe('AddLibraryModal relative path derivation', () => {
  it('shows relative path (full minus root) when preview returns fullPath and root configured', async () => {
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
        stubs: modalStubs,
      },
    })

    await wrapper.setProps({ visible: true })
    // allow watchers / async ops
    await delay(10)

    const input = wrapper.find('input.relative-input')
    expect(input.exists()).toBe(true)
    expect((input.element as HTMLInputElement).value).toBe('Author\\Title')
  })
})
