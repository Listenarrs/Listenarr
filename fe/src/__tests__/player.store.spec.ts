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
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import type { PlaybackState } from '@/types'

const getPlaybackMock = vi.fn()
const savePlaybackMock = vi.fn()

vi.mock('@/services/api', () => ({
  apiService: {
    getPlayback: (...args: unknown[]) => getPlaybackMock(...args),
    savePlayback: (...args: unknown[]) => savePlaybackMock(...args),
    streamUrl: (id: number, idx: number) => `/audiobooks/${id}/files/${idx}/stream`,
  },
}))

// Import after mock is registered
import { usePlayerStore } from '@/stores/player'

function makeState(overrides: Partial<PlaybackState> = {}): PlaybackState {
  return {
    audiobookId: 1,
    title: 'Test Book',
    asin: null,
    files: [
      { index: 0, durationSeconds: 100, contentType: 'audio/mpeg' },
      { index: 1, durationSeconds: 200, contentType: 'audio/mpeg' },
      { index: 2, durationSeconds: 150, contentType: 'audio/mpeg' },
    ],
    fileIndex: 0,
    positionSeconds: 0,
    finished: false,
    ...overrides,
  }
}

describe('player store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    getPlaybackMock.mockReset()
    savePlaybackMock.mockReset()
    savePlaybackMock.mockResolvedValue(undefined)
    localStorage.clear()
  })

  // 1. nextFile() past the last file returns false and leaves fileIndex unchanged
  it('nextFile() returns false and keeps fileIndex when already at last file', () => {
    const store = usePlayerStore()
    store.current = makeState({ fileIndex: 2 })
    store.fileIndex = 2

    const result = store.nextFile()

    expect(result).toBe(false)
    expect(store.fileIndex).toBe(2)
  })

  // 2. nextFile() on non-last file increments index and resets positionSeconds
  it('nextFile() increments fileIndex and resets positionSeconds on non-last file', () => {
    const store = usePlayerStore()
    store.current = makeState()
    store.fileIndex = 0
    store.positionSeconds = 42

    const result = store.nextFile()

    expect(result).toBe(true)
    expect(store.fileIndex).toBe(1)
    expect(store.positionSeconds).toBe(0)
  })

  // 3a. onEnded() on non-last file advances by one
  it('onEnded() on non-last file advances fileIndex', () => {
    const store = usePlayerStore()
    store.current = makeState()
    store.fileIndex = 0

    store.onEnded()

    expect(store.fileIndex).toBe(1)
  })

  // 3b. onEnded() on last file sets finished=true and calls savePlayback with finished:true
  it('onEnded() on last file sets finished and saves with finished:true', async () => {
    const store = usePlayerStore()
    store.current = makeState({ audiobookId: 7 })
    store.fileIndex = 2 // last file

    store.onEnded()

    await vi.waitFor(() => {
      expect(savePlaybackMock).toHaveBeenCalledWith(
        7,
        expect.objectContaining({ finished: true }),
      )
    })
  })

  // 4. setRate clamps and persists to localStorage
  it('setRate(5) clamps to 3.5 and writes localStorage', () => {
    const store = usePlayerStore()
    store.setRate(5)
    expect(store.rate).toBe(3.5)
    expect(localStorage.getItem('player.rate')).toBe('3.5')
  })

  it('setRate(0.1) clamps to 0.5 and writes localStorage', () => {
    const store = usePlayerStore()
    store.setRate(0.1)
    expect(store.rate).toBe(0.5)
    expect(localStorage.getItem('player.rate')).toBe('0.5')
  })

  // 5. Throttle: two save() calls within 10s result in exactly one savePlayback call
  it('save() throttles: two calls within 10s produce exactly one apiService.savePlayback', () => {
    const store = usePlayerStore()
    store.current = makeState({ audiobookId: 3 })
    store.fileIndex = 1
    store.positionSeconds = 55

    // Override the injectable now() so both calls land at the same timestamp
    let fakeNow = 1_000_000
    store._setNowFn(() => fakeNow)

    store.save()
    store.save() // same fake timestamp — should be de-duped

    expect(savePlaybackMock).toHaveBeenCalledTimes(1)
  })

  // 6. load(7) calls getPlayback(7) and populates store state
  it('load(7) calls getPlayback and sets current/fileIndex/positionSeconds', async () => {
    const state = makeState({ audiobookId: 7, fileIndex: 1, positionSeconds: 30 })
    getPlaybackMock.mockResolvedValue(state)

    const store = usePlayerStore()
    await store.load(7)

    expect(getPlaybackMock).toHaveBeenCalledWith(7)
    expect(store.current).toEqual(state)
    expect(store.fileIndex).toBe(1)
    expect(store.positionSeconds).toBe(30)
  })
})
