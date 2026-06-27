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
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { apiService } from '@/services/api'
import type { PlaybackState } from '@/types'

const RATE_KEY = 'player.rate'
const THROTTLE_MS = 10_000

function clampRate(r: number): number {
  return Math.min(3.5, Math.max(0.5, r))
}

function readStoredRate(): number {
  try {
    const raw = localStorage.getItem(RATE_KEY)
    if (raw !== null) return clampRate(parseFloat(raw))
  } catch {
    // localStorage unavailable (SSR / private mode)
  }
  return 1
}

// ponytail: module-level so tests can override via _setNowFn
let _now = () => Date.now()

export const usePlayerStore = defineStore('player', () => {
  const current = ref<PlaybackState | null>(null)
  const fileIndex = ref(0)
  const positionSeconds = ref(0)
  const playing = ref(false)
  const duration = ref(0)
  const rate = ref(readStoredRate())

  // Throttle state
  let lastSaveAt = -Infinity

  async function load(id: number): Promise<void> {
    const state = await apiService.getPlayback(id)
    current.value = state
    fileIndex.value = state.fileIndex
    positionSeconds.value = state.positionSeconds
  }

  function nextFile(): boolean {
    if (!current.value) return false
    if (fileIndex.value >= current.value.files.length - 1) return false
    fileIndex.value++
    positionSeconds.value = 0
    return true
  }

  function prevFile(): boolean {
    if (!current.value) return false
    if (fileIndex.value <= 0) return false
    fileIndex.value--
    positionSeconds.value = 0
    return true
  }

  function onEnded(): void {
    const advanced = nextFile()
    if (!advanced) {
      // Last file finished
      flush(true)
    }
    // If advanced, playing continues; component will handle the new src
  }

  function setRate(r: number): void {
    const clamped = clampRate(r)
    rate.value = clamped
    try {
      localStorage.setItem(RATE_KEY, String(clamped))
    } catch {
      // localStorage unavailable
    }
  }

  function save(): void {
    if (!current.value) return
    const t = _now()
    if (t - lastSaveAt < THROTTLE_MS) return
    lastSaveAt = t
    apiService
      .savePlayback(current.value.audiobookId, {
        fileIndex: fileIndex.value,
        positionSeconds: positionSeconds.value,
        finished: false,
      })
      .catch(() => {
        // best-effort progress save; ignore transient failures
      })
  }

  async function flush(finished = false): Promise<void> {
    if (!current.value) return
    lastSaveAt = _now()
    await apiService.savePlayback(current.value.audiobookId, {
      fileIndex: fileIndex.value,
      positionSeconds: positionSeconds.value,
      finished,
    })
  }

  // Test seam: replace the time source
  function _setNowFn(fn: () => number): void {
    _now = fn
  }

  return {
    current,
    fileIndex,
    positionSeconds,
    playing,
    duration,
    rate,
    load,
    nextFile,
    prevFile,
    onEnded,
    setRate,
    save,
    flush,
    _setNowFn,
  }
})
