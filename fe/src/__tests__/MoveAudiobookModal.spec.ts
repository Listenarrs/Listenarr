/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { describe, expect, it, vi } from 'vitest'
import MoveAudiobookModal from '@/components/feedback/MoveAudiobookModal.vue'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'
import { apiService } from '@/services/api'

function setFilesystemReadiness(ready: boolean) {
  useFilesystemReadinessStore().readiness = {
    isReady: true,
    status: 'ready',
    databaseConnected: true,
    migrationsCurrent: true,
    errorCode: null,
    filesystemReady: ready,
    filesystemStatus: ready ? 'Ready' : 'Running',
    filesystemPhase: ready ? null : 'AudiobookFileIdentities',
    filesystemErrorCode: null,
    filesystemErrorMessage: null,
  }
}

describe('MoveAudiobookModal filesystem readiness', () => {
  it('states the source-file disposition separately from folder cleanup', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    setFilesystemReadiness(true)
    const wrapper = mount(MoveAudiobookModal, {
      props: {
        visible: true,
        pendingMove: {
          original: '/downloads/Author/Book',
          combined: '/audiobooks/Author/Book',
        },
        moveFiles: true,
      },
      global: { plugins: [pinia] },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('Source files')
    expect(wrapper.text()).toContain('will be retained')
    expect(wrapper.text()).toContain('Remove empty source folder after move')
  })

  it('keeps path-only updates available but disables physical moves while initializing', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    setFilesystemReadiness(false)
    const wrapper = mount(MoveAudiobookModal, {
      props: {
        visible: true,
        pendingRootPath: 'D:\\Audiobooks',
        moveFiles: true,
      },
      global: { plugins: [pinia] },
    })

    const moveFiles = wrapper.get('input[aria-label="Move files now"]')
    expect(moveFiles.attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('filesystem initialization completes')
    expect(wrapper.get('.btn.btn-primary').text()).toBe('Update Path')

    await wrapper.get('.btn.btn-primary').trigger('click')

    expect(wrapper.emitted('confirm')?.[0]?.[0]).toMatchObject({
      moveFiles: false,
    })
  })

  it('allows physical moves after filesystem initialization completes', () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    setFilesystemReadiness(true)
    const wrapper = mount(MoveAudiobookModal, {
      props: {
        visible: true,
        pendingRootPath: 'D:\\Audiobooks',
        moveFiles: true,
      },
      global: { plugins: [pinia] },
    })

    expect(wrapper.get('input[aria-label="Move files now"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('.btn.btn-primary').text()).toBe('Move Files')
  })

  it('retains the managed library root when moving root-level files', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    setFilesystemReadiness(true)
    const wrapper = mount(MoveAudiobookModal, {
      props: {
        visible: true,
        pendingRootPath: 'D:\\Audiobooks\\Author\\Book',
        moveFiles: true,
        deleteEmpty: true,
        allowDeleteEmpty: false,
      },
      global: { plugins: [pinia] },
    })

    expect(wrapper.find('input[aria-label="Remove empty source folder"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('Managed root folder')
    expect(wrapper.text()).toContain('will remain')

    await wrapper.get('.btn.btn-primary').trigger('click')

    expect(wrapper.emitted('confirm')?.[0]?.[0]).toMatchObject({
      moveFiles: true,
      deleteEmpty: false,
    })
  })

  it('shows verified deletion while preserving the managed root', async () => {
    vi.mocked(apiService.checkVolume).mockResolvedValueOnce({
      sameVolume: false,
      willBreakHardlinks: true,
      verifiedSourceDeletionEnabled: true,
      sourceIsManagedRoot: true,
      sourceCleanupMessage:
        'Source files will be removed after every copied file is verified. The managed root folder will remain.',
    })
    const pinia = createPinia()
    setActivePinia(pinia)
    setFilesystemReadiness(true)
    const wrapper = mount(MoveAudiobookModal, {
      props: {
        visible: true,
        pendingMove: { original: '/audiobooks', combined: '/downloads/test' },
        moveFiles: true,
        allowDeleteEmpty: false,
      },
      global: { plugins: [pinia] },
    })

    await flushPromises()

    expect(wrapper.text()).toContain('removed after every copied file is verified')
    expect(wrapper.text()).toContain('/audiobooks will remain')
    expect(wrapper.find('input[aria-label="Remove empty source folder"]').exists()).toBe(false)
  })

  it('describes same-volume source disposition as a move', async () => {
    vi.mocked(apiService.checkVolume).mockResolvedValueOnce({
      sameVolume: true,
      willBreakHardlinks: false,
      verifiedSourceDeletionEnabled: false,
      sourceIsManagedRoot: false,
    })
    const pinia = createPinia()
    setActivePinia(pinia)
    setFilesystemReadiness(true)
    const wrapper = mount(MoveAudiobookModal, {
      props: {
        visible: true,
        pendingMove: { original: '/audiobooks/old', combined: '/audiobooks/new' },
        moveFiles: true,
      },
      global: { plugins: [pinia] },
    })

    await flushPromises()

    expect(wrapper.text()).toContain('Source files will be moved into the new location.')
  })

  it('prioritizes forced retention over a same-volume display', async () => {
    vi.mocked(apiService.checkVolume).mockResolvedValueOnce({
      sameVolume: true,
      willBreakHardlinks: false,
      verifiedSourceDeletionEnabled: false,
      forceCopyAndRetainSource: true,
      sourceIsManagedRoot: false,
    })
    const pinia = createPinia()
    setActivePinia(pinia)
    setFilesystemReadiness(true)
    const wrapper = mount(MoveAudiobookModal, {
      props: {
        visible: true,
        pendingMove: { original: '/readonly/old', combined: '/writable/new' },
        moveFiles: true,
      },
      global: { plugins: [pinia] },
    })

    await flushPromises()

    expect(wrapper.text()).toContain('Source files will be retained')
    expect(wrapper.text()).not.toContain('Source files will be moved into the new location.')
  })
})
