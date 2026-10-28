/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { apiService } from '@/services/api'
import { logger } from '@/utils/logger'
import type { SystemReadiness } from '@/types'

const POLL_INTERVAL_MS = 1500

export const useFilesystemReadinessStore = defineStore('filesystemReadiness', () => {
  const readiness = ref<SystemReadiness | null>(null)
  const loading = ref(false)
  let pollTimer: ReturnType<typeof setTimeout> | null = null
  let generation = 0

  const filesystemStatus = computed(() => readiness.value?.filesystemStatus ?? ('Pending' as const))
  const filesystemReady = computed(() => readiness.value?.filesystemReady === true)
  const filesystemInitializing = computed(
    () => filesystemStatus.value === 'Pending' || filesystemStatus.value === 'Running',
  )
  const filesystemFailed = computed(() => filesystemStatus.value === 'Failed')

  function clearTimer() {
    if (pollTimer != null) {
      clearTimeout(pollTimer)
      pollTimer = null
    }
  }

  function scheduleNext(expectedGeneration: number) {
    clearTimer()
    if (!filesystemInitializing.value || expectedGeneration !== generation) {
      return
    }

    pollTimer = setTimeout(() => {
      void refresh(expectedGeneration)
    }, POLL_INTERVAL_MS)
  }

  async function refresh(expectedGeneration: number = generation) {
    if (expectedGeneration !== generation) {
      return
    }

    loading.value = true
    try {
      const next = await apiService.getSystemReadiness()
      if (expectedGeneration !== generation) {
        return
      }

      readiness.value = next
    } catch (error) {
      logger.debug('Failed to refresh filesystem initialization state', error)
    } finally {
      if (expectedGeneration === generation) {
        loading.value = false
        scheduleNext(expectedGeneration)
      }
    }
  }

  function start() {
    generation += 1
    clearTimer()
    void refresh(generation)
  }

  function stop() {
    generation += 1
    clearTimer()
  }

  return {
    readiness,
    loading,
    filesystemStatus,
    filesystemReady,
    filesystemInitializing,
    filesystemFailed,
    start,
    stop,
    refresh,
  }
})
