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
import type { PlaybackState, Chapter, Bookmark } from '@/types'

const getPlaybackMock = vi.fn()
const savePlaybackMock = vi.fn()
const getBookmarksMock = vi.fn()
const addBookmarkMock = vi.fn()
const deleteBookmarkMock = vi.fn()

vi.mock('@/services/api', () => ({
  apiService: {
    getPlayback: (...args: unknown[]) => getPlaybackMock(...args),
    savePlayback: (...args: unknown[]) => savePlaybackMock(...args),
    streamUrl: (id: number, idx: number) => `/audiobooks/${id}/files/${idx}/stream`,
    getBookmarks: (...args: unknown[]) => getBookmarksMock(...args),
    addBookmark: (...args: unknown[]) => addBookmarkMock(...args),
    deleteBookmark: (...args: unknown[]) => deleteBookmarkMock(...args),
  },
}))

// Import after mock is registered
import { usePlayerStore } from '@/stores/player'

function makeChapters(): Chapter[] {
  return [
    { index: 0, fileIndex: 0, startSeconds: 0, endSeconds: 60, title: 'Chapter One' },
    { index: 1, fileIndex: 0, startSeconds: 60, endSeconds: 120, title: 'Chapter Two' },
    { index: 2, fileIndex: 1, startSeconds: 0, endSeconds: 90, title: 'Chapter Three' },
  ]
}

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
    chapters: [],
    ...overrides,
  }
}

function makeBookmark(overrides: Partial<Bookmark> = {}): Bookmark {
  return {
    id: 1,
    fileIndex: 0,
    positionSeconds: 42,
    label: 'My mark',
    createdUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

describe('player store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    getPlaybackMock.mockReset()
    savePlaybackMock.mockReset()
    savePlaybackMock.mockResolvedValue(undefined)
    getBookmarksMock.mockReset()
    addBookmarkMock.mockReset()
    deleteBookmarkMock.mockReset()
    localStorage.clear()
  })

  // --- Existing tests ---

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

  // --- Volume ---

  it('setVolume clamps to [0, 1] and persists to localStorage', () => {
    const store = usePlayerStore()
    store.setVolume(1.5)
    expect(store.volume).toBe(1)
    expect(localStorage.getItem('player.volume')).toBe('1')

    store.setVolume(-0.1)
    expect(store.volume).toBe(0)
  })

  it('toggleMute flips muted', () => {
    const store = usePlayerStore()
    expect(store.muted).toBe(false)
    store.toggleMute()
    expect(store.muted).toBe(true)
    store.toggleMute()
    expect(store.muted).toBe(false)
  })

  // --- Chapter computed ---

  it('currentChapter returns null when chapters are empty', () => {
    const store = usePlayerStore()
    store.current = makeState({ chapters: [] })
    store.fileIndex = 0
    store.positionSeconds = 30
    expect(store.currentChapter).toBeNull()
  })

  it('currentChapter returns the chapter whose range contains the current position', () => {
    const store = usePlayerStore()
    const chapters = makeChapters()
    store.current = makeState({ chapters })
    store.fileIndex = 0
    store.positionSeconds = 70 // falls in chapter index 1 (60–120)
    expect(store.currentChapter?.index).toBe(1)
  })

  it('currentChapter uses fallback: last chapter on same file with start <= pos', () => {
    const store = usePlayerStore()
    const chapters = makeChapters()
    store.current = makeState({ chapters })
    store.fileIndex = 0
    store.positionSeconds = 125 // past chapter 1's endSeconds but no exact match
    expect(store.currentChapter?.index).toBe(1) // fallback to last chapter on file 0
  })

  it('currentChapter is null when no chapter on current fileIndex covers the position', () => {
    const store = usePlayerStore()
    const chapters = makeChapters()
    store.current = makeState({ chapters })
    store.fileIndex = 2 // no chapters on file 2
    store.positionSeconds = 10
    expect(store.currentChapter).toBeNull()
  })

  // --- skipToChapter ---

  it('skipToChapter updates positionSeconds and emits seekRequest for same-file jump', () => {
    const store = usePlayerStore()
    const chapters = makeChapters()
    store.current = makeState({ chapters })
    store.fileIndex = 0
    store.positionSeconds = 10

    store.skipToChapter(chapters[1]!) // ch index 1, same file

    expect(store.positionSeconds).toBe(60)
    expect(store.fileIndex).toBe(0) // unchanged
    expect(store._seekRequest).not.toBeNull()
    expect(store._seekRequest!.position).toBe(60)
  })

  it('skipToChapter updates fileIndex for cross-file chapter jump', () => {
    const store = usePlayerStore()
    const chapters = makeChapters()
    store.current = makeState({ chapters })
    store.fileIndex = 0
    store.positionSeconds = 10

    store.skipToChapter(chapters[2]!) // ch index 2, fileIndex 1

    expect(store.fileIndex).toBe(1)
    expect(store.positionSeconds).toBe(0)
  })

  // --- nextChapter / prevChapter ---

  it('nextChapter advances to the next chapter', () => {
    const store = usePlayerStore()
    const chapters = makeChapters()
    store.current = makeState({ chapters })
    store.fileIndex = 0
    store.positionSeconds = 30 // in chapter 0

    store.nextChapter()

    expect(store.positionSeconds).toBe(60) // chapter 1 start
  })

  it('prevChapter goes to current chapter start when >3s in', () => {
    const store = usePlayerStore()
    const chapters = makeChapters()
    store.current = makeState({ chapters })
    store.fileIndex = 0
    store.positionSeconds = 70 // 10s into chapter 1 (start=60)

    store.prevChapter()

    expect(store.positionSeconds).toBe(60) // rewind to chapter 1 start
  })

  it('prevChapter goes to previous chapter when <=3s in', () => {
    const store = usePlayerStore()
    const chapters = makeChapters()
    store.current = makeState({ chapters })
    store.fileIndex = 0
    store.positionSeconds = 62 // 2s into chapter 1

    store.prevChapter()

    expect(store.positionSeconds).toBe(0) // chapter 0 start
  })

  // --- Sleep timer ---

  it('setSleepTimer("off") clears the timer', () => {
    const store = usePlayerStore()
    store._setNowFn(() => 1_000_000)
    store.setSleepTimer(30) // set 30 min
    expect(store.sleepTimerMode).toBe('timed')

    store.setSleepTimer('off')
    expect(store.sleepTimerMode).toBe('off')
    expect(store.sleepTimerEndsAt).toBeNull()
  })

  it('setSleepTimer(minutes) sets timed mode and endsAt', () => {
    const store = usePlayerStore()
    const fakeNow = 1_000_000
    store._setNowFn(() => fakeNow)
    store.setSleepTimer(15)
    expect(store.sleepTimerMode).toBe('timed')
    expect(store.sleepTimerEndsAt).toBe(fakeNow + 15 * 60_000)
  })

  it('setSleepTimer("chapter") sets chapter mode', () => {
    const store = usePlayerStore()
    store.setSleepTimer('chapter')
    expect(store.sleepTimerMode).toBe('chapter')
  })

  it('checkSleepTrigger returns true and clears timer when timed timer expires', () => {
    const store = usePlayerStore()
    let fakeNow = 1_000_000
    store._setNowFn(() => fakeNow)
    store.setSleepTimer(1) // 1 minute

    fakeNow += 70_000 // 70 seconds later — past the 60s timer
    const triggered = store.checkSleepTrigger()
    expect(triggered).toBe(true)
    expect(store.sleepTimerMode).toBe('off')
  })

  it('checkSleepTrigger returns false when timer has not expired', () => {
    const store = usePlayerStore()
    let fakeNow = 1_000_000
    store._setNowFn(() => fakeNow)
    store.setSleepTimer(5) // 5 minutes

    fakeNow += 10_000 // only 10s later
    expect(store.checkSleepTrigger()).toBe(false)
  })

  it('checkSleepTrigger returns true on chapter mode when positionSeconds >= chapter endSeconds', () => {
    const store = usePlayerStore()
    const chapters = makeChapters()
    store.current = makeState({ chapters })
    store.fileIndex = 0
    store.positionSeconds = 30 // in chapter 0 (endSeconds=60)
    store.setSleepTimer('chapter')

    expect(store.checkSleepTrigger()).toBe(false)

    store.positionSeconds = 60 // at endSeconds of chapter 0
    expect(store.checkSleepTrigger()).toBe(true)
    expect(store.sleepTimerMode).toBe('off')
  })

  it('sleepTimerRemainingSeconds returns 0 when off', () => {
    const store = usePlayerStore()
    expect(store.sleepTimerRemainingSeconds()).toBe(0)
  })

  it('sleepTimerRemainingSeconds returns approximate remaining time', () => {
    const store = usePlayerStore()
    let fakeNow = 1_000_000
    store._setNowFn(() => fakeNow)
    store.setSleepTimer(1) // 1 minute = 60 000 ms
    fakeNow += 10_000 // 10s elapsed
    expect(store.sleepTimerRemainingSeconds()).toBe(50) // ~50s left
  })

  // --- Bookmarks ---

  it('loadBookmarks() fetches and stores bookmarks', async () => {
    const store = usePlayerStore()
    store.current = makeState({ audiobookId: 5 })
    const bms = [makeBookmark({ id: 1 }), makeBookmark({ id: 2 })]
    getBookmarksMock.mockResolvedValue(bms)

    await store.loadBookmarks()

    expect(getBookmarksMock).toHaveBeenCalledWith(5)
    expect(store.bookmarks).toEqual(bms)
  })

  it('addBookmarkHere() posts and appends the new bookmark', async () => {
    const store = usePlayerStore()
    store.current = makeState({ audiobookId: 5 })
    store.fileIndex = 0
    store.positionSeconds = 77
    const newBm = makeBookmark({ id: 99, positionSeconds: 77, label: 'test' })
    addBookmarkMock.mockResolvedValue(newBm)

    await store.addBookmarkHere('test')

    expect(addBookmarkMock).toHaveBeenCalledWith(5, {
      fileIndex: 0,
      positionSeconds: 77,
      label: 'test',
    })
    expect(store.bookmarks).toContainEqual(newBm)
  })

  it('removeBookmark() calls deleteBookmark and removes from list', async () => {
    const store = usePlayerStore()
    store.current = makeState({ audiobookId: 5 })
    store.bookmarks = [makeBookmark({ id: 10 }), makeBookmark({ id: 20 })]
    deleteBookmarkMock.mockResolvedValue(undefined)

    await store.removeBookmark(10)

    expect(deleteBookmarkMock).toHaveBeenCalledWith(5, 10)
    expect(store.bookmarks.find((b) => b.id === 10)).toBeUndefined()
    expect(store.bookmarks.length).toBe(1)
  })

  it('jumpToBookmark() updates positionSeconds and emits seekRequest for same-file jump', () => {
    const store = usePlayerStore()
    store.current = makeState()
    store.fileIndex = 0
    store.positionSeconds = 10
    const bm = makeBookmark({ fileIndex: 0, positionSeconds: 88 })

    store.jumpToBookmark(bm)

    expect(store.positionSeconds).toBe(88)
    expect(store.fileIndex).toBe(0)
    expect(store._seekRequest?.position).toBe(88)
  })

  it('jumpToBookmark() changes fileIndex for cross-file bookmark', () => {
    const store = usePlayerStore()
    store.current = makeState()
    store.fileIndex = 0
    const bm = makeBookmark({ fileIndex: 1, positionSeconds: 33 })

    store.jumpToBookmark(bm)

    expect(store.fileIndex).toBe(1)
    expect(store.positionSeconds).toBe(33)
  })

  // --- Mark finished ---

  it('markFinished() sets finished=true and calls savePlayback with finished:true', async () => {
    const store = usePlayerStore()
    store.current = makeState({ audiobookId: 9 })

    await store.markFinished()

    expect(store.finished).toBe(true)
    expect(savePlaybackMock).toHaveBeenCalledWith(9, expect.objectContaining({ finished: true }))
  })

  // --- Collapse / close ---

  it('collapsed defaults to false when no localStorage entry', () => {
    const store = usePlayerStore()
    expect(store.collapsed).toBe(false)
  })

  it('toggleCollapsed() flips collapsed to true and persists "1" to localStorage', () => {
    const store = usePlayerStore()
    store.toggleCollapsed()
    expect(store.collapsed).toBe(true)
    expect(localStorage.getItem('player.collapsed')).toBe('1')
  })

  it('toggleCollapsed() twice restores collapsed to false and persists "0"', () => {
    const store = usePlayerStore()
    store.toggleCollapsed()
    store.toggleCollapsed()
    expect(store.collapsed).toBe(false)
    expect(localStorage.getItem('player.collapsed')).toBe('0')
  })

  it('collapsed initializes to true when localStorage contains "1"', () => {
    localStorage.setItem('player.collapsed', '1')
    // Re-create store to pick up stored value
    setActivePinia(createPinia())
    const store = usePlayerStore()
    expect(store.collapsed).toBe(true)
  })

  it('close() sets playing=false and current=null', async () => {
    const store = usePlayerStore()
    store.current = makeState({ audiobookId: 3 })
    store.playing = true
    savePlaybackMock.mockResolvedValue(undefined)

    await store.close()

    expect(store.playing).toBe(false)
    expect(store.current).toBeNull()
  })

  it('close() is best-effort: does not throw when flush fails', async () => {
    const store = usePlayerStore()
    store.current = makeState({ audiobookId: 3 })
    savePlaybackMock.mockRejectedValue(new Error('network error'))

    await expect(store.close()).resolves.toBeUndefined()
    expect(store.current).toBeNull()
  })
})
