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
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import RenamePreviewModal from '@/components/domain/organize/RenamePreviewModal.vue'
import { apiService } from '@/services/api'
import type { RenamePreview } from '@/types'
import { modalStubs } from '@/test/stubs'

vi.mock('@/services/api', () => ({
  apiService: {
    previewRename: vi.fn(),
  },
}))

describe('RenamePreviewModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders a cleaner before-and-after preview for organize changes', async () => {
    vi.mocked(apiService.previewRename).mockResolvedValue([
      {
        audiobookId: 7,
        audiobookTitle: 'Alchemised',
        currentFolderPath: 'D:\\test\\Author\\Alchemised',
        newFolderPath: 'D:\\test\\Author\\Alchemised test',
        folderChanged: true,
        hasChanges: true,
        fileRenames: [
          {
            fileId: 71,
            currentPath: 'D:\\test\\Author\\Alchemised\\Alchemised.m4b',
            newPath: 'D:\\test\\Author\\Alchemised test\\Alchemised test.m4b',
            currentFilename: 'Alchemised.m4b',
            newFilename: 'Alchemised test.m4b',
            changed: true,
          },
        ],
      },
    ] satisfies RenamePreview[])

    const wrapper = mount(RenamePreviewModal, {
      props: {
        visible: true,
        audiobookIds: [7],
      },
      global: {
        stubs: modalStubs,
      },
    })

    await flushPromises()

    expect(apiService.previewRename).toHaveBeenCalledWith([7])
    expect(wrapper.text()).toContain('1 of 1 audiobook(s) selected')
    expect(wrapper.text()).toContain(
      'Review the proposed folder and filename changes before organizing files on disk.',
    )
    expect(wrapper.text()).toContain('Folder + 1 file')
    expect(wrapper.findAll('.rename-path-value--current')).toHaveLength(2)
    expect(wrapper.findAll('.rename-path-value--new')).toHaveLength(2)
    expect(wrapper.text()).toContain('Current')
    expect(wrapper.text()).toContain('New')
    expect(wrapper.find('.btn.btn-primary').text()).toContain('Organize 1')
  })
})
