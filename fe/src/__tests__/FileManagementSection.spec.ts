import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'

describe('FileManagementSection', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('emits update:settings on pattern and select changes', async () => {
    const { default: FileManagementSection } = await import('@/components/settings/FileManagementSection.vue')
    const wrapper = mount(FileManagementSection, {
      props: {
        settings: {
          folderNamingPattern: '{Author}/{Series}/{Title}',
          fileNamingPattern: '{Title}',
          completedFileAction: 'Move',
        },
      },
    })

    const folderInput = wrapper.find('input[placeholder="{Author}/{Series}/{Title}"]')
    await folderInput.setValue('{Author}/{Title}')
    let last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.folderNamingPattern).toBe('{Author}/{Title}')

    const fileInput = wrapper.find('input[placeholder="{Title}"]')
    await fileInput.setValue('{Title}-{DiskNumber}')
    last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.fileNamingPattern).toBe('{Title}-{DiskNumber}')

    const sel = wrapper.find('select')
    await sel.setValue('Hardlink/Copy')
    last = wrapper.emitted()['update:settings']![wrapper.emitted()['update:settings']!.length - 1][0]
    expect(last.completedFileAction).toBe('Hardlink/Copy')
  })

  it('shows preview for multi-file pattern with chapter numbers', async () => {
    const { default: FileManagementSection } = await import('@/components/settings/FileManagementSection.vue')
    const wrapper = mount(FileManagementSection, {
      props: {
        settings: {
          multiFileNamingPattern: '{Title}-Ch{ChapterNumber:00}'
        }
      }
    })

    // Preview should show simulated chapter numbers 01/02/03
    const preview = wrapper.find('.pattern-preview code')
    expect(preview.exists()).toBe(true)
    expect(preview.text()).toContain('Ch01')
    expect(preview.text()).toContain('Ch02')
    expect(preview.text()).toContain('Ch03')
  })
})
