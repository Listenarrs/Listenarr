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
import { ref, computed } from 'vue'
import { apiService } from '@/services/api'
import type { PlaybackState, Chapter, Bookmark } from '@/types'

const RATE_KEY = 'player.rate'
const VOL_KEY = 'player.volume'
const THROTTLE_MS = 10_000

function clampRate(r: number): number {
  return Math.min(3.5, Math.max(0.5, r))
}

function clampVolume(v: number): number {
  return Math.min(1, Math.max(0, v))
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

function readStoredVolume(): number {
  try {
    const raw = localStorage.getItem(VOL_KEY)
    if (raw !== null) return clampVolume(parseFloat(raw))
  } catch {
    // localStorage unavailable
  }
  return 1
}

// ponytail: module-level so tests can override via _setNowFn
let _now = () => Date.now()

export type SleepTimerOption = 'off' | 'chapter' | number // number = minutes

export const usePlayerStore = defineStore('player', () => {
  const current = ref<PlaybackState | null>(null)
  const fileIndex = ref(0)
  const positionSeconds = ref(0)
  const playing = ref(false)
  const duration = ref(0)
  const rate = ref(readStoredRate())
  const volume = ref(readStoredVolume())
  const muted = ref(false)
  const finished = ref(false)

  // Bookmarks for the current audiobook
  const bookmarks = ref<Bookmark[]>([])

  // Sleep timer
  const sleepTimerMode = ref<'off' | 'timed' | 'chapter'>('off')
  const sleepTimerEndsAt = ref<number | null>(null) // epoch ms for timed mode
  // For 'chapter' mode: the audio positionSeconds at which we should stop (set when timer is activated)
  const _chapterSleepTarget = ref<number | null>(null)

  // Internal seek request: component watches and seeks audio element
  // Version bump forces the watcher to fire even for repeated seeks to same position
  const _seekRequest = ref<{ position: number; version: number } | null>(null)

  // Throttle state
  let lastSaveAt = -Infinity

  // --- Chapters ---

  const chapters = computed(() => current.value?.chapters ?? [])

  const currentChapter = computed((): Chapter | null => {
    const chs = chapters.value
    const fi = fileIndex.value
    const pos = positionSeconds.value

    // Primary: exact range match (startSeconds <= pos < endSeconds)
    const exact = chs.find(
      (ch) => ch.fileIndex === fi && ch.startSeconds <= pos && pos < ch.endSeconds,
    )
    if (exact) return exact

    // Fallback: last chapter with matching fileIndex whose start <= pos
    const candidates = chs.filter((ch) => ch.fileIndex === fi && ch.startSeconds <= pos)
    if (candidates.length > 0) return candidates[candidates.length - 1]!

    return null
  })

  function skipToChapter(ch: Chapter): void {
    positionSeconds.value = ch.startSeconds
    if (fileIndex.value !== ch.fileIndex) {
      fileIndex.value = ch.fileIndex
      // onLoadedMetadata in component will seek to positionSeconds (ch.startSeconds)
    } else {
      // Same file: signal the component to seek the audio element
      _emitSeek(ch.startSeconds)
    }
  }

  function nextChapter(): void {
    const chs = chapters.value
    const cur = currentChapter.value
    if (!cur) return
    const idx = chs.indexOf(cur)
    if (idx < chs.length - 1) skipToChapter(chs[idx + 1]!)
  }

  function prevChapter(): void {
    const chs = chapters.value
    const cur = currentChapter.value
    if (!cur) return
    // If >3s into current chapter go to its start; else go to previous chapter
    if (positionSeconds.value - cur.startSeconds > 3) {
      skipToChapter(cur)
    } else {
      const idx = chs.indexOf(cur)
      if (idx > 0) skipToChapter(chs[idx - 1]!)
    }
  }

  // --- Seek signal ---

  function _emitSeek(pos: number): void {
    _seekRequest.value = {
      position: pos,
      version: (_seekRequest.value?.version ?? 0) + 1,
    }
  }

  // --- Load ---

  async function load(id: number): Promise<void> {
    const state = await apiService.getPlayback(id)
    current.value = state
    fileIndex.value = state.fileIndex
    positionSeconds.value = state.positionSeconds
    finished.value = state.finished
    bookmarks.value = []
    sleepTimerMode.value = 'off'
    sleepTimerEndsAt.value = null
  }

  // --- File navigation ---

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
    // If advanced, playing continues; component handles the new src
  }

  // --- Playback rate ---

  function setRate(r: number): void {
    const clamped = clampRate(r)
    rate.value = clamped
    try {
      localStorage.setItem(RATE_KEY, String(clamped))
    } catch {
      // localStorage unavailable
    }
  }

  // --- Volume ---

  function setVolume(v: number): void {
    const clamped = clampVolume(v)
    volume.value = clamped
    try {
      localStorage.setItem(VOL_KEY, String(clamped))
    } catch {
      // localStorage unavailable
    }
  }

  function toggleMute(): void {
    muted.value = !muted.value
  }

  // --- Progress save ---

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

  async function flush(fin = false): Promise<void> {
    if (!current.value) return
    lastSaveAt = _now()
    await apiService.savePlayback(current.value.audiobookId, {
      fileIndex: fileIndex.value,
      positionSeconds: positionSeconds.value,
      finished: fin,
    })
  }

  async function markFinished(): Promise<void> {
    finished.value = true
    await flush(true)
  }

  // --- Sleep timer ---

  function setSleepTimer(opt: SleepTimerOption): void {
    if (opt === 'off') {
      sleepTimerMode.value = 'off'
      sleepTimerEndsAt.value = null
      _chapterSleepTarget.value = null
    } else if (opt === 'chapter') {
      sleepTimerMode.value = 'chapter'
      sleepTimerEndsAt.value = null
      // Capture the current chapter's endSeconds now; triggers when we reach it
      _chapterSleepTarget.value = currentChapter.value?.endSeconds ?? null
    } else {
      // opt is minutes
      sleepTimerMode.value = 'timed'
      sleepTimerEndsAt.value = _now() + (opt as number) * 60_000
      _chapterSleepTarget.value = null
    }
  }

  /** Returns remaining seconds for a timed timer; 0 when off or expired. */
  function sleepTimerRemainingSeconds(): number {
    if (sleepTimerMode.value !== 'timed' || sleepTimerEndsAt.value === null) return 0
    return Math.max(0, Math.round((sleepTimerEndsAt.value - _now()) / 1000))
  }

  /**
   * Called by the component on each timeupdate (or equivalent tick).
   * Returns true if the audio should sleep now.
   */
  function checkSleepTrigger(): boolean {
    if (sleepTimerMode.value === 'timed' && sleepTimerEndsAt.value !== null) {
      if (_now() >= sleepTimerEndsAt.value) {
        sleepTimerMode.value = 'off'
        sleepTimerEndsAt.value = null
        return true
      }
    }
    if (sleepTimerMode.value === 'chapter' && _chapterSleepTarget.value !== null) {
      if (positionSeconds.value >= _chapterSleepTarget.value) {
        sleepTimerMode.value = 'off'
        _chapterSleepTarget.value = null
        return true
      }
    }
    return false
  }

  // --- Bookmarks ---

  async function loadBookmarks(): Promise<void> {
    if (!current.value) return
    bookmarks.value = await apiService.getBookmarks(current.value.audiobookId)
  }

  async function addBookmarkHere(label?: string): Promise<void> {
    if (!current.value) return
    const bm = await apiService.addBookmark(current.value.audiobookId, {
      fileIndex: fileIndex.value,
      positionSeconds: positionSeconds.value,
      label: label ?? null,
    })
    bookmarks.value = [...bookmarks.value, bm]
  }

  async function removeBookmark(id: number): Promise<void> {
    if (!current.value) return
    await apiService.deleteBookmark(current.value.audiobookId, id)
    bookmarks.value = bookmarks.value.filter((b) => b.id !== id)
  }

  function jumpToBookmark(b: Bookmark): void {
    positionSeconds.value = b.positionSeconds
    if (fileIndex.value !== b.fileIndex) {
      fileIndex.value = b.fileIndex
      // onLoadedMetadata in component will seek to positionSeconds
    } else {
      _emitSeek(b.positionSeconds)
    }
  }

  // --- Test seams ---

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
    volume,
    muted,
    finished,
    bookmarks,
    sleepTimerMode,
    sleepTimerEndsAt,
    chapters,
    currentChapter,
    _seekRequest,
    load,
    nextFile,
    prevFile,
    onEnded,
    setRate,
    setVolume,
    toggleMute,
    save,
    flush,
    markFinished,
    setSleepTimer,
    sleepTimerRemainingSeconds,
    checkSleepTrigger,
    skipToChapter,
    nextChapter,
    prevChapter,
    loadBookmarks,
    addBookmarkHere,
    removeBookmark,
    jumpToBookmark,
    _setNowFn,
  }
})
