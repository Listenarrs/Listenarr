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
    getAudiobook: vi.fn().mockImplementation(async (id: number) => ({ id })),
    getQualityProfiles: vi.fn().mockResolvedValue([]),
    getApplicationSettings: vi.fn().mockResolvedValue({ outputPath: 'C:\\root' }),
    getAudiobookIdentifiers: vi.fn().mockResolvedValue({ identifiers: [] }),
    checkVolume: vi.fn().mockResolvedValue({ sameVolume: true }),
    updateAudiobook: vi.fn().mockResolvedValue({ message: 'ok', audiobook: {} }),
    updateAudiobookIdentifiers: vi.fn().mockResolvedValue({ identifiers: [] }),
    moveAudiobook: vi.fn().mockResolvedValue({ message: 'queued', jobId: 'job-1' }),
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ info: vi.fn(), success: vi.fn(), error: vi.fn() }),
}))

vi.mock('@/services/signalr', () => ({
  signalRService: {
    onMoveJobUpdate: vi.fn(() => () => {}),
  },
}))

import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'

const audiobook = {
  id: 1,
  title: 'Sample',
  authors: ['Author'],
  basePath: 'C:\\root\\Some Author\\Some Title',
  monitored: true,
  tags: [],
}

describe('EditAudiobookModal move options', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('Change without moving should update audiobook and not call move API', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    // let init settle
    await new Promise((r) => setTimeout(r, 200))

    // Ensure there is a detectable change: set an explicit custom root and flip monitored
    ;(wrapper.vm as unknown).selectedRootId = 0
    ;(wrapper.vm as unknown).customRootPath = 'C:\\root\\New Author\\New Book'
    ;(wrapper.vm as unknown).formData.monitored = false
    await wrapper.vm.$nextTick()

    // Start save flow and resolve the in-component confirmation promise by
    // calling the module-scoped resolver if it was created. This avoids
    // relying on modal rendering in jsdom.
    const savePromise = (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver) resolver({ proceed: true, moveFiles: false, deleteEmptySource: false })
    await savePromise
    // Allow async work to settle
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(0)
  })

  it('Move should call move API with deleteEmptySource true by default', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))

    // Ensure there is a detectable change: set an explicit custom root and flip monitored
    ;(wrapper.vm as unknown).selectedRootId = 0
    ;(wrapper.vm as unknown).customRootPath = 'C:\\root\\New Author\\New Book'
    ;(wrapper.vm as unknown).formData.monitored = false
    await wrapper.vm.$nextTick()

    // Start save flow and resolve the in-component confirmation promise to
    // simulate the user choosing to move files now.
    const savePromise2 = (wrapper.vm as unknown).handleSave()
    await new Promise((r) => setTimeout(r, 10))
    const resolver2 = (wrapper.vm as unknown).moveConfirmResolver
    if (resolver2) resolver2({ proceed: true, moveFiles: true, deleteEmptySource: true })
    await savePromise2

    // Wait for async update + move to settle
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.moveAudiobook).toHaveBeenCalledTimes(1)
    expect(apiService.moveAudiobook).toHaveBeenCalledWith(
      expect.anything(),
      expect.anything(),
      expect.objectContaining({ moveFiles: true, deleteEmptySource: true }),
    )
  })

  it('Edition-only changes should persist through updateAudiobook', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))
    ;(wrapper.vm as unknown as { formData: { edition: string } }).formData.edition =
      'Revised Edition'
    await wrapper.vm.$nextTick()

    await (wrapper.vm as unknown as { handleSave: () => Promise<void> }).handleSave()
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.objectContaining({ edition: 'Revised Edition' }),
    )
  })

  it('metadata changes should persist through updateAudiobook', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook: {
          ...audiobook,
          subtitle: 'Original Subtitle',
          narrators: ['Original Narrator'],
          description: 'Original description',
          publisher: 'Original Publisher',
          language: 'english',
          publishedDate: '2024-01-15',
          publishYear: '2024',
          runtime: 600,
          series: 'Original Series',
          seriesNumber: '1',
          genres: ['Fantasy'],
          imageUrl: 'https://example.com/original.jpg',
        },
      },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))

    const vm = wrapper.vm as unknown as {
      formData: {
        title: string
        subtitle: string
        authors: string[]
        narrators: string[]
        description: string
        publisher: string
        language: string
        publishedDate: string
        publishYear: string
        runtime: string
        edition: string
        seriesMemberships: Array<{
          seriesName: string
          seriesNumber: string
          isPrimary: boolean
        }>
        genres: string[]
        imageUrl: string
      }
      handleSave: () => Promise<void>
    }

    vm.formData.title = 'Edited Title'
    vm.formData.subtitle = 'Edited Subtitle'
    vm.formData.authors = ['Edited Author']
    vm.formData.narrators = ['Edited Narrator']
    vm.formData.description = 'Edited description'
    vm.formData.publisher = 'Edited Publisher'
    vm.formData.language = 'swedish'
    vm.formData.publishedDate = '2025-02-01'
    vm.formData.publishYear = '2025'
    vm.formData.runtime = '720'
    vm.formData.edition = 'Collector Edition'
    vm.formData.seriesMemberships = [
      { seriesName: 'Edited Universe', seriesNumber: '4', isPrimary: true },
      { seriesName: 'Anthology Line', seriesNumber: '12', isPrimary: false },
    ]
    vm.formData.genres = ['Sci-Fi', 'Adventure']
    vm.formData.imageUrl = 'https://example.com/edited.jpg'
    await wrapper.vm.$nextTick()

    await vm.handleSave()
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.objectContaining({
        title: 'Edited Title',
        subtitle: 'Edited Subtitle',
        authors: ['Edited Author'],
        narrators: ['Edited Narrator'],
        description: 'Edited description',
        publisher: 'Edited Publisher',
        language: 'Swedish',
        publishedDate: '2025-02-01',
        publishYear: '2025',
        runtime: 720,
        edition: 'Collector Edition',
        series: 'Edited Universe',
        seriesNumber: '4',
        seriesMemberships: [
          expect.objectContaining({
            seriesName: 'Edited Universe',
            seriesNumber: '4',
            isPrimary: true,
            sortOrder: 0,
          }),
          expect.objectContaining({
            seriesName: 'Anthology Line',
            seriesNumber: '12',
            isPrimary: false,
            sortOrder: 1,
          }),
        ],
        genres: ['Sci-Fi', 'Adventure'],
        imageUrl: 'https://example.com/edited.jpg',
      }),
    )
  })

  it('persists a non-first primary series selection (regression for #658)', async () => {
    const wrapper = mount(EditAudiobookModal, {
      props: { isOpen: true, audiobook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 200))

    const vm = wrapper.vm as unknown as {
      formData: {
        seriesMemberships: Array<{ seriesName: string; seriesNumber: string; isPrimary: boolean }>
      }
      handleSave: () => Promise<void>
    }

    // User marks the SECOND series as primary; the bug previously reverted this to the first.
    vm.formData.seriesMemberships = [
      { seriesName: 'Publication Order', seriesNumber: '1', isPrimary: false },
      { seriesName: 'Chronological Order', seriesNumber: '3', isPrimary: true },
    ]
    await wrapper.vm.$nextTick()

    await vm.handleSave()
    await new Promise((r) => setTimeout(r, 50))

    const { apiService } = await import('@/services/api')
    expect(apiService.updateAudiobook).toHaveBeenCalledWith(
      1,
      expect.objectContaining({
        series: 'Chronological Order',
        seriesNumber: '3',
        seriesMemberships: [
          expect.objectContaining({
            seriesName: 'Publication Order',
            isPrimary: false,
            sortOrder: 0,
          }),
          expect.objectContaining({
            seriesName: 'Chronological Order',
            isPrimary: true,
            sortOrder: 1,
          }),
        ],
      }),
    )
  })

  it('hydrates current metadata immediately and renders person fields as tags', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getQualityProfiles).mockImplementation(() => new Promise(() => {}))
    vi.mocked(apiService.getAudiobook).mockResolvedValue({
      ...audiobook,
      subtitle: 'Existing Subtitle',
      narrators: ['Narrator One', 'Narrator Two'],
      description: 'Existing description',
      publisher: 'Existing Publisher',
      language: 'english',
      publishedDate: '2024-01-15',
      publishYear: '2024',
      runtime: 600,
      edition: 'First Edition',
      series: 'Existing Series',
      seriesNumber: '3',
      seriesMemberships: [
        { seriesName: 'Existing Series', seriesNumber: '3', isPrimary: true, sortOrder: 0 },
        { seriesName: 'Universe Collection', seriesNumber: '9', isPrimary: false, sortOrder: 1 },
      ],
      genres: ['Fantasy', 'Adventure'],
      imageUrl: 'https://example.com/current.jpg',
      tags: ['favorite'],
    })

    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 50))

    expect(wrapper.text()).toContain('Edit Audiobook: Sample')
    expect((wrapper.get('#metadata-title').element as HTMLInputElement).value).toBe('Sample')
    expect((wrapper.get('#metadata-subtitle').element as HTMLInputElement).value).toBe(
      'Existing Subtitle',
    )
    expect((wrapper.get('#metadata-description').element as HTMLTextAreaElement).value).toBe(
      'Existing description',
    )
    expect((wrapper.get('#metadata-publisher').element as HTMLInputElement).value).toBe(
      'Existing Publisher',
    )
    expect((wrapper.get('#metadata-language').element as HTMLInputElement).value).toBe('English')
    expect((wrapper.get('#metadata-published-date').element as HTMLInputElement).value).toBe(
      '2024-01-15',
    )

    const authorTags = wrapper.findAll('.author-tags-editor .tag-item').map((item) => item.text())
    expect(authorTags).toEqual(expect.arrayContaining(['Author']))

    const narratorTags = wrapper
      .findAll('.narrator-tags-editor .tag-item')
      .map((item) => item.text())
    expect(narratorTags).toEqual(expect.arrayContaining(['Narrator One', 'Narrator Two']))

    const genreTags = wrapper.findAll('.genre-tags-editor .tag-item').map((item) => item.text())
    expect(genreTags).toEqual(expect.arrayContaining(['Fantasy', 'Adventure']))
    expect((wrapper.get('#metadata-series-name-0').element as HTMLInputElement).value).toBe(
      'Existing Series',
    )
    expect((wrapper.get('#metadata-series-number-1').element as HTMLInputElement).value).toBe('9')
  })

  it('rehydrates unchanged metadata when the same audiobook receives fuller data', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getAudiobook).mockResolvedValue({
      ...audiobook,
      description: 'Loaded from refreshed detail payload',
      publishedDate: '2024-03-01',
      language: 'english',
      narrators: ['Narrator One'],
    })

    const wrapper = mount(EditAudiobookModal, {
      props: {
        isOpen: true,
        audiobook,
      },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await new Promise((r) => setTimeout(r, 50))

    await wrapper.setProps({
      audiobook: {
        ...audiobook,
        description: 'Loaded from refreshed detail payload',
        language: 'english',
        narrators: ['Narrator One'],
      },
    })
    await new Promise((r) => setTimeout(r, 50))

    expect((wrapper.get('#metadata-description').element as HTMLTextAreaElement).value).toBe(
      'Loaded from refreshed detail payload',
    )
    expect((wrapper.get('#metadata-published-date').element as HTMLInputElement).value).toBe(
      '2024-03-01',
    )
    expect((wrapper.get('#metadata-language').element as HTMLInputElement).value).toBe('English')
    expect(wrapper.findAll('.narrator-tags-editor .tag-item').map((item) => item.text())).toEqual(
      expect.arrayContaining(['Narrator One']),
    )
  })
})
