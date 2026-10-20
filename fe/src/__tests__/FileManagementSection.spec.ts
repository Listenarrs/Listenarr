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
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'

describe('FileManagementSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('emits update:settings on pattern and select changes', async () => {
    const { default: FileManagementSection } =
      await import('@/components/settings/FileManagementSection.vue')
    const wrapper = mount(FileManagementSection, {
      props: {
        settings: {
          folderNamingPattern: '{Author}/{Series}/{Title}',
          fileNamingPattern: '{Title}',
          completedFileAction: 'move',
          importBlacklistExtensions: ['.nfo'],
        },
      },
    })

    const folderInput = wrapper.find('input[placeholder="{Author}/{Series}/{Title}"]')
    await folderInput.setValue('{Author}/{Title}')
    let last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.folderNamingPattern).toBe('{Author}/{Title}')

    const fileInput = wrapper.find('input[placeholder="{Title}"]')
    await fileInput.setValue('{Title}-{DiskNumber}')
    last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.fileNamingPattern).toBe('{Title}-{DiskNumber}')

    const sel = wrapper.find('select')
    await sel.setValue('hardlink/copy')
    last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.completedFileAction).toBe('hardlink/copy')

    const textarea = wrapper.find('textarea')
    await textarea.setValue('.nfo\njpg')
    await textarea.trigger('change')
    last =
      wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.importBlacklistExtensions).toEqual(['.nfo', '.jpg'])
  })

  it('shows both File Metadata Enrichment options off, disabled and marked not available', async () => {
    const { default: FileManagementSection } =
      await import('@/components/settings/FileManagementSection.vue')
    const wrapper = mount(FileManagementSection, {
      props: {
        settings: { completedFileAction: 'move', importBlacklistExtensions: [] },
      },
    })

    const group = wrapper.find('[role="group"]')
    expect(group.text()).toContain('File Metadata Enrichment')

    const boxes = group.findAll('input[type="checkbox"]')
    expect(boxes).toHaveLength(2)
    for (const box of boxes) {
      expect((box.element as HTMLInputElement).checked).toBe(false)
      expect((box.element as HTMLInputElement).disabled).toBe(true)
    }

    const text = group.text()
    expect(text).toContain('Enable metadata enrichment')
    expect(text).toContain('Include hardlinked files')
    expect(text.match(/Not available yet/g)).toHaveLength(2)
    expect(text).toContain('share the same bytes')

    await boxes[0].trigger('change')
    expect(wrapper.emitted()['update:settings']).toBeUndefined()
  })

  it('shows preview for multi-file pattern with chapter numbers', async () => {
    const { default: FileManagementSection } =
      await import('@/components/settings/FileManagementSection.vue')
    const wrapper = mount(FileManagementSection, {
      props: {
        settings: {
          multiFileNamingPattern: '{Title}-Ch{ChapterNumber:00}',
        },
      },
    })

    // Preview should show simulated chapter numbers 01/02/03
    const preview = wrapper.find('.pattern-preview code')
    expect(preview.exists()).toBe(true)
    expect(preview.text()).toContain('Ch01')
    expect(preview.text()).toContain('Ch02')
    expect(preview.text()).toContain('Ch03')
  })

  it('shows path length warning when combined pattern exceeds 259 characters', async () => {
    const { default: FileManagementSection } =
      await import('@/components/settings/FileManagementSection.vue')
    // Use highly nested folder pattern that definitely exceeds 259 chars with sample values
    // Each {Author}/{Series}/{Title} expands to ~48 chars; six repetitions + output path + file will exceed 259
    const longPattern =
      '{Author}/{Series}/{Title}/{Author}/{Series}/{Title}/{Author}/{Series}/{Title}/{Author}/{Series}/{Title}/{Author}/{Series}/{Title}/{Author}/{Series}/{Title}'
    const wrapper = mount(FileManagementSection, {
      props: {
        settings: {
          outputPath: 'D:\\VeryLongAudiobookLibraryBasePath\\Collection',
          folderNamingPattern: longPattern,
          fileNamingPattern: '{Title}',
        },
      },
    })

    const warning = wrapper.find('.path-length-warning')
    expect(warning.exists()).toBe(true)
    expect(warning.text()).toContain('260 characters')
  })

  it('does not show path length warning when combined pattern is short', async () => {
    const { default: FileManagementSection } =
      await import('@/components/settings/FileManagementSection.vue')
    const wrapper = mount(FileManagementSection, {
      props: {
        settings: {
          outputPath: 'D:\\Books',
          folderNamingPattern: '{Author}/{Title}',
          fileNamingPattern: '{Title}',
        },
      },
    })

    const warning = wrapper.find('.path-length-warning')
    expect(warning.exists()).toBe(false)

    // Should show the "ok" indicator instead
    const ok = wrapper.find('.path-length-ok')
    expect(ok.exists()).toBe(true)
    expect(ok.text()).toContain('/ 259 characters')
  })
})
