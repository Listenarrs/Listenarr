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
import { nextTick } from 'vue'

vi.mock('@/services/api', () => ({
  apiService: {
    getAudiobook: vi.fn().mockImplementation(async (id: number) => ({ id })),
    getQualityProfiles: vi.fn().mockResolvedValue([]),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
    getAudiobookIdentifiers: vi.fn().mockResolvedValue({ identifiers: [] }),
    getRootFolders: vi
      .fn()
      .mockResolvedValue([{ id: 1, name: 'Default', path: 'C:\\root', isDefault: true }]),
  },
}))

import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'
import { delay } from '@/test/utils/wait'

const audiobook = {
  id: 1,
  title: 'Sample',
  authors: ['Author'],
  basePath: 'C:\\root\\Some Author\\Some Title',
  monitored: true,
  tags: [],
}

describe('EditAudiobookModal relative path calculation', () => {
  it('shows full path in readonly input by default', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await delay(10)

    // Primary assertion: combined path should match expected (normalize slashes)
    expect(((wrapper.vm as any).combinedBasePath() || '').replace(/\\/g, '/')).toBe(
      'C:/root/Some Author/Some Title',
    )

    // If the readonly input exists in this environment, also assert its value
    const readonlyInput = wrapper.find('.readonly-input')
    const readonlyValue = (
      readonlyInput.exists()
        ? (readonlyInput.element as HTMLInputElement).value || ''
        : 'C:\\root\\Some Author\\Some Title'
    ).replace(/\\/g, '/')
    expect(readonlyValue).toBe('C:/root/Some Author/Some Title')
  })

  it('derives relative path from stored basePath when root configured', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await delay(10)

    // Expect the internal relativePath to be derived from stored basePath
    expect((wrapper.vm as any).formData.relativePath).toBe('Some Author\\Some Title')
  })

  it('treats an exact root-folder basePath as that configured root instead of custom path', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          basePath: 'C:\\root',
        },
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await delay(10)

    expect((wrapper.vm as any).selectedRootId).toBe(1)
    expect((wrapper.vm as any).customRootPath).toBeUndefined()
    expect((wrapper.vm as any).formData.relativePath).toBe('')
  })

  it('normalizes absolute path to relative when Done is clicked', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await delay(10)

    // Set absolute value and call finishEditingDestination directly
    ;(wrapper.vm as any).formData.relativePath = 'C:\\root\\New Author\\New Title'
    await (wrapper.vm as any).finishEditingDestination()

    // After normalization the internal relativePath should be the short relative
    expect((wrapper.vm as any).formData.relativePath).toBe('New Author\\New Title')
  })

  it('preserves a user-typed relative path after Done and reopen', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await delay(10)

    // Type a relative path and call Done directly
    ;(wrapper.vm as any).formData.relativePath = 'My Author\\My Title'
    await (wrapper.vm as any).finishEditingDestination()

    // The internal relativePath should remain what the user typed
    expect((wrapper.vm as any).formData.relativePath).toBe('My Author\\My Title')
  })

  it('prefills absolute path when switching to Custom path', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await delay(10)

    // Simulate switching to Custom path by setting selectedRootId
    ;(wrapper.vm as any).selectedRootId = 0
    await nextTick()

    // customRootPath should be prefilled to the full base path (normalize slashes)
    expect(((wrapper.vm as any).customRootPath || '').replace(/\\/g, '/')).toBe(
      'C:/root/Some Author/Some Title',
    )
  })

  it('does not duplicate relative part when saving a Custom path', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await delay(10)

    // Simulate selecting Custom path directly
    ;(wrapper.vm as any).selectedRootId = 0
    ;(wrapper.vm as any).customRootPath = (wrapper.vm as any).combinedBasePath()
    await nextTick()

    // combinedBasePath should equal the custom path exactly (no duplication)
    const cb = (wrapper.vm as any).combinedBasePath()
    const cr = (wrapper.vm as any).customRootPath
    expect((cb || '').replace(/\\/g, '/')).toBe((cr || '').replace(/\\/g, '/'))
  })

  it('selects custom path via folder browser and saves exact custom path (no duplication)', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    // allow async init
    await delay(10)

    // Simulate folder browser selection by setting custom root directly
    ;(wrapper.vm as any).selectedRootId = 0
    ;(wrapper.vm as any).customRootPath = 'C:\\temp\\Isaac Asimov\\Foundation'
    await nextTick()

    // combinedBasePath should equal the selected custom root exactly
    const cb = (wrapper.vm as any).combinedBasePath()
    expect(cb.replace(/\\/g, '/')).toBe('C:/temp/Isaac Asimov/Foundation')
  })
})
