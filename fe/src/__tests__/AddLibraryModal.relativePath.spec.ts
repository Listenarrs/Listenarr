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
    addToLibrary: vi.fn().mockResolvedValue({ audiobook: { id: 1 } }),
  },
}))

import AddLibraryModal from '@/components/domain/audiobook/AddLibraryModal.vue'

const fakeBook = {
  title: 'Test Title',
  authors: ['Author One'],
  imageUrl: '',
  asin: 'B001234567',
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

describe('AddLibraryModal relative path derivation', () => {
  it('shows and submits the same normalized effective destination', async () => {
    const { apiService } = await import('@/services/api')
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    const input = wrapper.get('input.relative-input')
    await input.setValue('Author/Title')
    await wrapper.vm.$nextTick()

    const preview = wrapper.get('[data-testid="effective-destination"]').text()
    expect(preview).toContain('C:\\root\\Author\\Title')

    await (wrapper.vm as unknown as { addToLibrary: () => Promise<void> }).addToLibrary()

    expect(apiService.addToLibrary).toHaveBeenCalledWith(
      expect.any(Object),
      expect.objectContaining({ destinationPath: 'C:\\root\\Author\\Title' }),
    )
  })

  it('submits a configured-root relative destination whose Unix trailing whitespace is significant', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.addToLibrary).mockClear()
    vi.mocked(apiService.getApplicationSettings).mockResolvedValueOnce({ outputPath: '/library' })
    vi.mocked(apiService.previewLibraryPath).mockResolvedValueOnce({
      fullPath: '/library/Author/Title',
      relativePath: 'Author/Title',
    })
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    const vm = wrapper.vm as unknown as {
      options: { relativePath: string }
      addToLibrary: () => Promise<void>
    }
    vm.options.relativePath = 'Author/Title '
    await wrapper.vm.$nextTick()
    await vm.addToLibrary()

    expect(apiService.addToLibrary).toHaveBeenCalledTimes(1)
    expect(apiService.addToLibrary).toHaveBeenCalledWith(
      expect.any(Object),
      expect.objectContaining({ destinationPath: '/library/Author/Title ' }),
    )
    wrapper.unmount()
  })

  it('preserves a literal backslash in a Unix relative destination segment', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.addToLibrary).mockClear()
    vi.mocked(apiService.getApplicationSettings).mockResolvedValueOnce({ outputPath: '/library' })
    vi.mocked(apiService.previewLibraryPath).mockResolvedValueOnce({
      fullPath: '/library/Author/Title',
      relativePath: 'Author/Title',
    })
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    const vm = wrapper.vm as unknown as {
      options: { relativePath: string }
      addToLibrary: () => Promise<void>
    }
    vm.options.relativePath = 'Author\\Title'
    await wrapper.vm.$nextTick()
    await vm.addToLibrary()

    expect(apiService.addToLibrary).toHaveBeenCalledTimes(1)
    expect(apiService.addToLibrary).toHaveBeenCalledWith(
      expect.any(Object),
      expect.objectContaining({ destinationPath: '/library/Author\\Title' }),
    )
    wrapper.unmount()
  })

  it('does not offer an arbitrary custom-path destination', async () => {
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))

    expect(wrapper.text()).not.toContain('Custom path')
    expect(wrapper.find('.custom-path-input').exists()).toBe(false)
  })

  it('rejects rooted input instead of treating it as a hidden custom destination', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.addToLibrary).mockClear()
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    const input = wrapper.get('input.relative-input')
    await input.setValue('C:\\root\\Author\\Title')
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain(
      'Enter a path relative to the selected configured root folder.',
    )
    await (wrapper.vm as unknown as { addToLibrary: () => Promise<void> }).addToLibrary()
    expect(apiService.addToLibrary).not.toHaveBeenCalled()
  })

  it('recovers when the server violates the relativePath contract with an absolute path', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getApplicationSettings).mockResolvedValueOnce({ outputPath: 'C:\\root' })
    vi.mocked(apiService.previewLibraryPath).mockResolvedValueOnce({
      fullPath: 'C:\\root\\Author\\Title',
      relativePath: 'C:\\root\\Author\\Title',
    })
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const input = wrapper.get('input.relative-input')
    expect((input.element as HTMLInputElement).value).toBe('Author\\Title')
    expect(wrapper.text()).not.toContain(
      'Enter a path relative to the selected configured root folder.',
    )
    wrapper.unmount()
  })

  it('rejects an absolute server relativePath even when it uses a different path syntax than the selected root', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.getApplicationSettings).mockResolvedValue({ outputPath: '/library' })
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: '/library/Author/Title',
      relativePath: 'C:\\root\\Author\\Title',
    })
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const input = wrapper.get('input.relative-input')
    expect((input.element as HTMLInputElement).value).toBe('Author/Title')
    expect(wrapper.text()).not.toContain(
      'Enter a path relative to the selected configured root folder.',
    )
    wrapper.unmount()
    vi.mocked(apiService.getApplicationSettings).mockResolvedValue({ outputPath: 'C:\\root' })
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: 'C:\\root\\Author\\Title',
      relativePath: '',
    })
  })

  it('shows relative path (full minus root) when preview returns fullPath and root configured', async () => {
    const wrapper = mount(AddLibraryModal, {
      props: {
        visible: false,
        book: fakeBook,
      },
      attachTo: document.body,
      global: {
        plugins: [(await import('pinia')).createPinia()],
      },
    })

    await wrapper.setProps({ visible: true })
    // allow watchers / async ops
    await new Promise((r) => setTimeout(r, 10))

    const input = wrapper.find('input.relative-input')
    expect(input.exists()).toBe(true)
    expect((input.element as HTMLInputElement).value).toBe('Author\\Title')
  })

  it('rejects a Windows-rooted manual relative path under a Unix configured root', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.addToLibrary).mockClear()
    vi.mocked(apiService.getApplicationSettings).mockResolvedValue({ outputPath: '/library' })
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: '/library/Author/Title',
      relativePath: 'Author/Title',
    })
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: fakeBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    await wrapper.get('input.relative-input').setValue('C:\\Books\\Other')
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain(
      'Enter a path relative to the selected configured root folder.',
    )
    await (wrapper.vm as unknown as { addToLibrary: () => Promise<void> }).addToLibrary()
    expect(apiService.addToLibrary).not.toHaveBeenCalled()
    wrapper.unmount()
    vi.mocked(apiService.getApplicationSettings).mockResolvedValue({ outputPath: 'C:\\root' })
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: 'C:\\root\\Author\\Title',
      relativePath: '',
    })
  })

  it('ignores a stale preview response that completes after a newer preview', async () => {
    const { apiService } = await import('@/services/api')
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: fakeBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })
    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const older = deferred<{ fullPath: string; relativePath: string }>()
    const newer = deferred<{ fullPath: string; relativePath: string }>()
    vi.mocked(apiService.previewLibraryPath)
      .mockImplementationOnce(() => older.promise)
      .mockImplementationOnce(() => newer.promise)
    const vm = wrapper.vm as unknown as {
      refreshPreviewFromMetadata: (force?: boolean) => Promise<void>
    }
    const olderRequest = vm.refreshPreviewFromMetadata(true)
    const newerRequest = vm.refreshPreviewFromMetadata(true)
    newer.resolve({
      fullPath: 'C:\\root\\New\\Destination',
      relativePath: 'New\\Destination',
    })
    await newerRequest
    older.resolve({
      fullPath: 'C:\\root\\Old\\Destination',
      relativePath: 'Old\\Destination',
    })
    await olderRequest

    expect((wrapper.get('input.relative-input').element as HTMLInputElement).value).toBe(
      'New\\Destination',
    )
    wrapper.unmount()
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: 'C:\\root\\Author\\Title',
      relativePath: '',
    })
  })

  it('ignores a stale preview failure after a newer preview succeeds', async () => {
    const { apiService } = await import('@/services/api')
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: fakeBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })
    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const older = deferred<{ fullPath: string; relativePath: string }>()
    const newer = deferred<{ fullPath: string; relativePath: string }>()
    vi.mocked(apiService.previewLibraryPath)
      .mockImplementationOnce(() => older.promise)
      .mockImplementationOnce(() => newer.promise)
    const vm = wrapper.vm as unknown as {
      refreshPreviewFromMetadata: (force?: boolean) => Promise<void>
    }
    const olderRequest = vm.refreshPreviewFromMetadata(true)
    const newerRequest = vm.refreshPreviewFromMetadata(true)
    newer.resolve({
      fullPath: 'C:\\root\\New\\Destination',
      relativePath: 'New\\Destination',
    })
    await newerRequest
    older.reject(new Error('stale preview failed'))
    await olderRequest

    expect((wrapper.get('input.relative-input').element as HTMLInputElement).value).toBe(
      'New\\Destination',
    )
    expect(wrapper.text()).not.toContain('The destination preview could not be refreshed.')
    wrapper.unmount()
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: 'C:\\root\\Author\\Title',
      relativePath: '',
    })
  })

  it('does not let a preview from a closed modal session commit after reopen', async () => {
    const { apiService } = await import('@/services/api')
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: fakeBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })
    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const oldSession = deferred<{ fullPath: string; relativePath: string }>()
    vi.mocked(apiService.previewLibraryPath).mockImplementationOnce(() => oldSession.promise)
    const vm = wrapper.vm as unknown as {
      refreshPreviewFromMetadata: (force?: boolean) => Promise<void>
    }
    const oldRequest = vm.refreshPreviewFromMetadata(true)

    await wrapper.setProps({ visible: false })
    vi.mocked(apiService.previewLibraryPath).mockResolvedValueOnce({
      fullPath: 'C:\\root\\New\\Session',
      relativePath: 'New\\Session',
    })
    await wrapper.setProps({ visible: true })
    await vi.waitFor(() => {
      expect((wrapper.get('input.relative-input').element as HTMLInputElement).value).toBe(
        'New\\Session',
      )
    })

    oldSession.resolve({
      fullPath: 'C:\\root\\Old\\Session',
      relativePath: 'Old\\Session',
    })
    await oldRequest

    expect((wrapper.get('input.relative-input').element as HTMLInputElement).value).toBe(
      'New\\Session',
    )
    wrapper.unmount()
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: 'C:\\root\\Author\\Title',
      relativePath: '',
    })
  })

  it('does not allow the modal to close while an add commit is in flight', async () => {
    const { apiService } = await import('@/services/api')
    const pendingAdd = deferred<{ audiobook: { id: number; title: string } }>()
    vi.mocked(apiService.addToLibrary).mockImplementationOnce(() => pendingAdd.promise)
    const identifierlessBook = { ...fakeBook, asin: '', isbn: [], title: 'Identifierless Add' }
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: identifierlessBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    const vm = wrapper.vm as unknown as {
      addToLibrary: () => Promise<void>
      closeModal: () => void
      isAdding: boolean
    }
    const request = vm.addToLibrary()
    await vi.waitFor(() => expect(vm.isAdding).toBe(true))

    vm.closeModal()
    await wrapper.vm.$nextTick()

    expect(vm.isAdding).toBe(true)
    expect(wrapper.emitted('close')).toBeUndefined()
    const cancelButton = wrapper
      .findAll('button')
      .find((button) => button.text().includes('Cancel'))
    expect(cancelButton?.attributes('disabled')).toBeDefined()

    pendingAdd.resolve({ audiobook: { id: 9, title: identifierlessBook.title } })
    await request

    expect(vm.isAdding).toBe(false)
    expect(wrapper.emitted('added')).toHaveLength(1)
    expect(wrapper.emitted('close')).toHaveLength(1)
    wrapper.unmount()
    vi.mocked(apiService.addToLibrary).mockResolvedValue({ audiobook: { id: 1 } })
  })

  it('reconciles an already-existing destination conflict as an idempotent add success', async () => {
    const { apiService } = await import('@/services/api')
    const existing = { id: 42, title: 'Identifierless Existing Book' }
    vi.mocked(apiService.addToLibrary).mockRejectedValueOnce(
      Object.assign(new Error('API error: 409'), {
        status: 409,
        body: JSON.stringify({
          message: 'Audiobook already exists in library',
          audiobook: existing,
        }),
      }),
    )
    const identifierlessBook = {
      ...fakeBook,
      asin: '',
      isbn: [],
      title: 'Identifierless Existing Book',
    }
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: identifierlessBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    const vm = wrapper.vm as unknown as {
      addToLibrary: () => Promise<void>
      isAdding: boolean
    }

    await vm.addToLibrary()

    expect(wrapper.emitted('added')).toEqual([[existing]])
    expect(wrapper.emitted('close')).toHaveLength(1)
    expect(vm.isAdding).toBe(false)
    wrapper.unmount()
    vi.mocked(apiService.addToLibrary).mockResolvedValue({ audiobook: { id: 1 } })
  })

  it('does not let a stale add request close or unlock a newer modal session', async () => {
    const { apiService } = await import('@/services/api')
    const oldAdd = deferred<{ audiobook: { id: number; title: string; asin: string } }>()
    const newAdd = deferred<{ audiobook: { id: number; title: string; asin: string } }>()
    vi.mocked(apiService.addToLibrary)
      .mockImplementationOnce(() => oldAdd.promise)
      .mockImplementationOnce(() => newAdd.promise)
    const oldBook = { ...fakeBook, asin: 'OLD-ADD', title: 'Old Add Book' }
    const newBook = { ...fakeBook, asin: 'NEW-ADD', title: 'New Add Book' }
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: oldBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    const vm = wrapper.vm as unknown as {
      addToLibrary: () => Promise<void>
      isAdding: boolean
      destinationPathValidationError: string | null
    }
    const oldRequest = vm.addToLibrary()
    await vi.waitFor(() => expect(vm.isAdding).toBe(true))

    await wrapper.setProps({ visible: false })
    await wrapper.setProps({ book: newBook, visible: true })
    await vi.waitFor(() => {
      expect(vm.isAdding).toBe(false)
      expect(vm.destinationPathValidationError).toBeNull()
    })

    const newRequest = vm.addToLibrary()
    await vi.waitFor(() => expect(vm.isAdding).toBe(true))
    oldAdd.resolve({ audiobook: { id: 10, title: oldBook.title, asin: oldBook.asin } })
    await oldRequest

    expect(vm.isAdding).toBe(true)
    expect(wrapper.emitted('added')).toBeUndefined()
    expect(wrapper.emitted('close')).toBeUndefined()

    newAdd.resolve({ audiobook: { id: 11, title: newBook.title, asin: newBook.asin } })
    await newRequest

    expect(vm.isAdding).toBe(false)
    expect(wrapper.emitted('added')).toHaveLength(1)
    expect(wrapper.emitted('close')).toHaveLength(1)
    wrapper.unmount()
    vi.mocked(apiService.addToLibrary).mockResolvedValue({ audiobook: { id: 1 } })
  })

  it('does not let an in-flight automatic preview overwrite a manual relative-path edit', async () => {
    const { apiService } = await import('@/services/api')
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: fakeBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })
    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const automatic = deferred<{ fullPath: string; relativePath: string }>()
    vi.mocked(apiService.previewLibraryPath).mockImplementationOnce(() => automatic.promise)
    const vm = wrapper.vm as unknown as {
      refreshPreviewFromMetadata: (force?: boolean) => Promise<void>
    }
    const automaticRequest = vm.refreshPreviewFromMetadata(true)
    await wrapper.get('input.relative-input').setValue('Manual\\Destination')
    automatic.resolve({
      fullPath: 'C:\\root\\Automatic\\Destination',
      relativePath: 'Automatic\\Destination',
    })
    await automaticRequest

    expect((wrapper.get('input.relative-input').element as HTMLInputElement).value).toBe(
      'Manual\\Destination',
    )
    wrapper.unmount()
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: 'C:\\root\\Author\\Title',
      relativePath: '',
    })
  })

  it('fails closed when the current automatic destination preview cannot be refreshed', async () => {
    const { apiService } = await import('@/services/api')
    vi.mocked(apiService.addToLibrary).mockClear()
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: fakeBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })
    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))

    vi.mocked(apiService.previewLibraryPath).mockRejectedValueOnce(new Error('preview unavailable'))
    const vm = wrapper.vm as unknown as {
      refreshPreviewFromMetadata: (force?: boolean) => Promise<void>
      addToLibrary: () => Promise<void>
    }
    await vm.refreshPreviewFromMetadata(true)

    expect(wrapper.text()).toContain('The destination preview could not be refreshed.')
    await vm.addToLibrary()
    expect(apiService.addToLibrary).not.toHaveBeenCalled()
    wrapper.unmount()
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: 'C:\\root\\Author\\Title',
      relativePath: '',
    })
  })

  it('honors a user root change made while seed metadata is still loading', async () => {
    const { apiService } = await import('@/services/api')
    const metadata = deferred<Record<string, unknown>>()
    vi.mocked(apiService.getRootFolders).mockResolvedValue([
      { id: 1, name: 'One', path: 'C:\\one', isDefault: true },
      { id: 2, name: 'Two', path: 'C:\\two', isDefault: false },
    ])
    vi.mocked(apiService.getAudibleMetadata).mockReturnValue(metadata.promise)
    vi.mocked(apiService.previewLibraryPath).mockImplementation((_book, root) =>
      Promise.resolve(
        root === 'C:\\two'
          ? { fullPath: 'C:\\two\\Chosen\\Title', relativePath: 'Chosen\\Title' }
          : { fullPath: 'C:\\one\\Default\\Title', relativePath: 'Default\\Title' },
      ),
    )
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: fakeBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    await wrapper.get('.root-folder-select select').setValue('2')
    await new Promise((resolve) => setTimeout(resolve, 10))
    metadata.resolve({})
    await new Promise((resolve) => setTimeout(resolve, 10))

    expect((wrapper.get('input.relative-input').element as HTMLInputElement).value).toBe(
      'Chosen\\Title',
    )
    expect(wrapper.get('[data-testid="effective-destination"]').text()).toContain(
      'C:\\two\\Chosen\\Title',
    )
    wrapper.unmount()
    vi.mocked(apiService.getRootFolders).mockResolvedValue([])
    vi.mocked(apiService.getAudibleMetadata).mockResolvedValue({})
    vi.mocked(apiService.previewLibraryPath).mockResolvedValue({
      fullPath: 'C:\\root\\Author\\Title',
      relativePath: '',
    })
  })

  it('preserves user metadata edits made while seed enrichment is still loading', async () => {
    const { apiService } = await import('@/services/api')
    const metadata = deferred<Record<string, unknown>>()
    vi.mocked(apiService.getAudibleMetadata).mockReturnValue(metadata.promise)
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: fakeBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    await wrapper.get('.metadata-toggle-btn').trigger('click')
    const titleInput = wrapper.get('.metadata-editor input.form-input')
    await titleInput.setValue('User Edited Title')
    metadata.resolve({
      metadata: {
        asin: fakeBook.asin,
        title: 'Late Enriched Title',
        authors: [{ name: 'Late Author' }],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 10))

    expect(
      (wrapper.get('.metadata-editor input.form-input').element as HTMLInputElement).value,
    ).toBe('User Edited Title')
    const vm = wrapper.vm as unknown as { editableMetadata: { title?: string } }
    expect(vm.editableMetadata.title).toBe('User Edited Title')
    wrapper.unmount()
    vi.mocked(apiService.getAudibleMetadata).mockResolvedValue({})
  })

  it('does not let metadata enrichment from a previous book overwrite the current book', async () => {
    const { apiService } = await import('@/services/api')
    const oldMetadata = deferred<Record<string, unknown>>()
    const oldBook = { ...fakeBook, asin: 'OLD-ASIN', title: 'Old Book' }
    const newBook = { ...fakeBook, asin: 'NEW-ASIN', title: 'New Book' }
    vi.mocked(apiService.getAudibleMetadata).mockImplementation((asin: string) => {
      if (asin === oldBook.asin) return oldMetadata.promise
      return Promise.resolve({
        metadata: {
          asin: newBook.asin,
          title: 'New Enriched Book',
          authors: [{ name: 'New Author' }],
        },
      })
    })
    const wrapper = mount(AddLibraryModal, {
      props: { visible: false, book: oldBook },
      attachTo: document.body,
      global: { plugins: [(await import('pinia')).createPinia()] },
    })

    await wrapper.setProps({ visible: true })
    await new Promise((resolve) => setTimeout(resolve, 10))
    await wrapper.setProps({ book: newBook })
    await new Promise((resolve) => setTimeout(resolve, 10))
    oldMetadata.resolve({
      metadata: {
        asin: oldBook.asin,
        title: 'Old Enriched Book',
        authors: [{ name: 'Old Author' }],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 10))

    const vm = wrapper.vm as unknown as { editableMetadata: { title?: string; asin?: string } }
    expect(vm.editableMetadata.title).toBe('New Enriched Book')
    expect(vm.editableMetadata.asin).toBe(newBook.asin)
    wrapper.unmount()
    vi.mocked(apiService.getAudibleMetadata).mockResolvedValue({})
  })
})
