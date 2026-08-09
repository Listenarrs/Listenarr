/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { describe, expect, it } from 'vitest'
import MoveAudiobookModal from '@/components/feedback/MoveAudiobookModal.vue'
import { useFilesystemReadinessStore } from '@/stores/filesystemReadiness'

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
})
