<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<template>
  <div
    v-if="player.current"
    class="audio-player"
    :class="{ 'audio-player--pill': player.collapsed }"
    role="region"
    aria-label="Audio player"
  >
    <!-- Audio element always present when current is set — drives playback regardless of collapsed state -->
    <audio
      ref="el"
      :src="src"
      @loadedmetadata="onLoadedMetadata"
      @timeupdate="onTimeUpdate"
      @ended="onEnded"
      @play="player.playing = true"
      @pause="onPause"
    />

    <!-- Collapsed pill: cover + play/pause + restore + close -->
    <div v-if="player.collapsed" class="pill-inner">
      <img v-if="coverUrl" :src="coverUrl" alt="" class="pill-cover" aria-hidden="true" />
      <span class="pill-title" :title="player.current.title ?? ''">
        {{ player.current.title ?? '' }}
      </span>
      <button
        class="nav-btn player-btn player-btn--play"
        @click="togglePlay"
        :aria-label="player.playing ? 'Pause' : 'Play'"
        :aria-pressed="player.playing"
      >
        <PhPause v-if="player.playing" weight="fill" />
        <PhPlay v-else weight="fill" />
      </button>
      <button
        class="nav-btn player-btn"
        @click="player.toggleCollapsed()"
        aria-label="Restore player"
        title="Restore player"
      >
        <PhCaretUp />
      </button>
      <button
        class="nav-btn player-btn player-btn--sm"
        @click="player.close()"
        aria-label="Close player"
        title="Close player"
      >
        <PhX />
      </button>
    </div>

    <!-- Full bar: scrub bar + controls (hidden when collapsed) -->
    <template v-else>
    <!-- Progress row: elapsed · scrub · total (integrated into the bar body) -->
    <div class="player-progress">
      <span class="player-time">{{ formatTime(player.positionSeconds) }}</span>
      <input
        type="range"
        class="scrub-bar"
        min="0"
        :max="player.duration || 0"
        v-model.number="player.positionSeconds"
        @input="onScrubInput"
        aria-label="Playback position"
      />
      <span class="player-time">{{ formatTime(player.duration) }}</span>
    </div>

    <div class="player-inner">
      <!-- Left: cover + title + chapter -->
      <div class="player-meta">
        <img
          v-if="coverUrl"
          :src="coverUrl"
          alt=""
          class="player-cover"
          aria-hidden="true"
        />
        <div class="player-info">
          <div class="player-title" :title="player.current.title ?? ''">
            {{ player.current.title ?? 'Unknown title' }}
          </div>
          <div v-if="player.currentChapter" class="player-chapter" :title="player.currentChapter.title">
            {{ player.currentChapter.title }}
          </div>
        </div>
      </div>

      <!-- Center: main playback controls -->
      <div class="player-controls-wrap">
        <div class="player-controls" role="group" aria-label="Playback controls">
          <!-- Prev chapter -->
          <button
            v-if="player.chapters.length > 0"
            class="nav-btn player-btn"
            @click="player.prevChapter()"
            aria-label="Previous chapter"
            title="Previous chapter"
          >
            <PhCaretLeft weight="bold" />
          </button>

          <button class="nav-btn player-btn" @click="prevFile" aria-label="Previous part">
            <PhSkipBack />
          </button>
          <button class="nav-btn player-btn" @click="rewind10" aria-label="Skip back 10 seconds">
            <PhRewind />
          </button>
          <button
            class="nav-btn player-btn player-btn--play"
            @click="togglePlay"
            :aria-label="player.playing ? 'Pause' : 'Play'"
            :aria-pressed="player.playing"
          >
            <PhPause v-if="player.playing" weight="fill" />
            <PhPlay v-else weight="fill" />
          </button>
          <button class="nav-btn player-btn" @click="forward30" aria-label="Skip forward 30 seconds">
            <PhFastForward />
          </button>
          <button class="nav-btn player-btn" @click="nextFile" aria-label="Next part">
            <PhSkipForward />
          </button>

          <!-- Next chapter -->
          <button
            v-if="player.chapters.length > 0"
            class="nav-btn player-btn"
            @click="player.nextChapter()"
            aria-label="Next chapter"
            title="Next chapter"
          >
            <PhCaretRight weight="bold" />
          </button>
        </div>
      </div>

      <!-- Right: extra controls -->
      <div class="player-right">
        <!-- Volume -->
        <div class="volume-wrap">
          <button
            class="nav-btn player-btn player-btn--sm"
            @click="player.toggleMute()"
            :aria-label="player.muted ? 'Unmute' : 'Mute'"
            :title="player.muted ? 'Unmute' : 'Mute'"
          >
            <PhSpeakerX v-if="player.muted || player.volume === 0" />
            <PhSpeakerLow v-else-if="player.volume < 0.5" />
            <PhSpeakerHigh v-else />
          </button>
          <input
            type="range"
            class="volume-slider"
            min="0"
            max="1"
            step="0.05"
            :value="player.muted ? 0 : player.volume"
            @input="onVolumeInput"
            aria-label="Volume"
          />
        </div>

        <!-- Chapters popover -->
        <div v-if="player.chapters.length > 0" class="popover-wrap" ref="chapterWrapRef">
          <button
            class="nav-btn player-btn player-btn--sm"
            @click="toggleChapterList"
            :aria-expanded="chapterListOpen"
            aria-label="Chapter list"
            title="Chapters"
          >
            <PhListBullets />
          </button>
          <div v-if="chapterListOpen" class="player-popover chapter-popover" role="dialog" aria-label="Chapters">
            <div class="popover-header">Chapters</div>
            <ul class="popover-list" role="list">
              <li
                v-for="ch in player.chapters"
                :key="ch.index"
                class="popover-item"
                :class="{ active: player.currentChapter?.index === ch.index }"
                role="listitem"
              >
                <button
                  class="popover-item-btn"
                  @click="jumpToChapter(ch)"
                >
                  <span class="popover-item-label">{{ ch.title }}</span>
                  <span class="popover-item-time">{{ formatTime(ch.startSeconds) }}</span>
                </button>
              </li>
            </ul>
          </div>
        </div>

        <!-- Sleep timer -->
        <div class="popover-wrap" ref="sleepWrapRef">
          <button
            class="nav-btn player-btn player-btn--sm"
            :class="{ 'player-btn--active': player.sleepTimerMode !== 'off' }"
            @click="toggleSleepMenu"
            :aria-expanded="sleepMenuOpen"
            aria-label="Sleep timer"
            title="Sleep timer"
          >
            <PhTimer />
            <span v-if="player.sleepTimerMode === 'timed'" class="timer-badge">
              {{ formatSleepRemaining() }}
            </span>
          </button>
          <div v-if="sleepMenuOpen" class="player-popover sleep-popover" role="dialog" aria-label="Sleep timer">
            <div class="popover-header">Sleep timer</div>
            <ul class="popover-list" role="list">
              <li
                v-for="opt in SLEEP_OPTIONS"
                :key="opt.value"
                class="popover-item"
                :class="{ active: isSleepOptActive(opt.value) }"
              >
                <button class="popover-item-btn" @click="setSleep(opt.value)">
                  {{ opt.label }}
                </button>
              </li>
            </ul>
          </div>
        </div>

        <!-- Bookmarks -->
        <div class="popover-wrap" ref="bookmarkWrapRef">
          <button
            class="nav-btn player-btn player-btn--sm"
            @click="onBookmarkBtn"
            aria-label="Bookmark"
            title="Add bookmark / view bookmarks"
          >
            <PhBookmarkSimple />
          </button>
          <div v-if="bookmarkPanelOpen" class="player-popover bookmark-popover" role="dialog" aria-label="Bookmarks">
            <div class="popover-header">
              Bookmarks
              <button class="popover-add-btn" @click="addBookmark" aria-label="Add bookmark here">
                <PhPlus />
              </button>
            </div>
            <!-- Optional label input -->
            <div v-if="bookmarkLabelInputOpen" class="bookmark-label-wrap">
              <input
                ref="bookmarkLabelRef"
                v-model="bookmarkLabel"
                class="bookmark-label-input"
                type="text"
                placeholder="Label (optional)"
                @keydown.enter="confirmAddBookmark"
                @keydown.escape="bookmarkLabelInputOpen = false"
                maxlength="80"
                aria-label="Bookmark label"
              />
              <button class="popover-add-btn" @click="confirmAddBookmark" aria-label="Save bookmark">
                <PhCheck />
              </button>
            </div>
            <ul v-if="player.bookmarks.length > 0" class="popover-list" role="list">
              <li
                v-for="bm in player.bookmarks"
                :key="bm.id"
                class="popover-item"
              >
                <button class="popover-item-btn" @click="player.jumpToBookmark(bm)">
                  <span class="popover-item-label">{{ bm.label ?? formatTime(bm.positionSeconds) }}</span>
                  <span class="popover-item-time">{{ formatTime(bm.positionSeconds) }}</span>
                </button>
                <button
                  class="popover-del-btn"
                  @click.stop="player.removeBookmark(bm.id)"
                  :aria-label="`Delete bookmark ${bm.label ?? formatTime(bm.positionSeconds)}`"
                >
                  <PhTrash />
                </button>
              </li>
            </ul>
            <div v-else class="popover-empty">No bookmarks yet</div>
          </div>
        </div>

        <!-- Mark finished -->
        <button
          class="nav-btn player-btn player-btn--sm"
          :class="{ 'player-btn--active': player.finished }"
          @click="onMarkFinished"
          :aria-label="player.finished ? 'Marked as finished' : 'Mark as finished'"
          :title="player.finished ? 'Marked as finished' : 'Mark as finished'"
        >
          <PhCheckCircle :weight="player.finished ? 'fill' : 'regular'" />
        </button>

        <!-- Speed selector -->
        <div class="player-speed">
          <label class="player-speed-label" for="player-speed">Speed</label>
          <select
            id="player-speed"
            class="speed-select"
            :value="player.rate"
            @change="onRateChange"
            aria-label="Playback speed"
          >
            <option v-for="s in SPEEDS" :key="s" :value="s">{{ s }}x</option>
          </select>
        </div>

        <!-- Minimize player -->
        <button
          class="nav-btn player-btn player-btn--sm"
          @click="player.toggleCollapsed()"
          aria-label="Minimize player"
          title="Minimize player"
        >
          <PhCaretDown />
        </button>

        <!-- Close player -->
        <button
          class="nav-btn player-btn player-btn--sm"
          @click="player.close()"
          aria-label="Close player"
          title="Close player"
        >
          <PhX />
        </button>
      </div>
    </div>
    </template>
  </div>
</template>

<script setup lang="ts">
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
import { ref, computed, watch, onMounted, onUnmounted, nextTick } from 'vue'
import {
  PhPlay,
  PhPause,
  PhSkipBack,
  PhSkipForward,
  PhRewind,
  PhFastForward,
  PhCaretLeft,
  PhCaretRight,
  PhCaretDown,
  PhCaretUp,
  PhX,
  PhListBullets,
  PhTimer,
  PhBookmarkSimple,
  PhPlus,
  PhCheck,
  PhTrash,
  PhCheckCircle,
  PhSpeakerHigh,
  PhSpeakerLow,
  PhSpeakerX,
} from '@phosphor-icons/vue'
import { usePlayerStore } from '@/stores/player'
import type { Chapter } from '@/types'
import { apiService } from '@/services/api'
import { buildApiPath } from '@/services/apiBase'

const SPEEDS = [0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3]

type SleepOpt = 'off' | 'chapter' | number
interface SleepOption { label: string; value: SleepOpt }
const SLEEP_OPTIONS: SleepOption[] = [
  { label: 'Off', value: 'off' },
  { label: '5 min', value: 5 },
  { label: '15 min', value: 15 },
  { label: '30 min', value: 30 },
  { label: '45 min', value: 45 },
  { label: '60 min', value: 60 },
  { label: 'End of chapter', value: 'chapter' },
]

const player = usePlayerStore()

const el = ref<HTMLAudioElement | null>(null)

// Recompute src whenever the active file changes; the <audio> element reloads automatically
const src = computed(() => {
  if (!player.current) return ''
  return apiService.streamUrl(player.current.audiobookId, player.fileIndex)
})

// Cover image via ASIN — same pattern as library store image URLs
const coverUrl = computed(() => {
  const asin = player.current?.asin
  if (!asin) return ''
  return buildApiPath('/images/' + encodeURIComponent(asin))
})

// h:mm:ss formatter used for position and duration display
function formatTime(sec: number): string {
  const s = Math.max(0, Math.floor(sec))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const ss = s % 60
  return `${h}:${String(m).padStart(2, '0')}:${String(ss).padStart(2, '0')}`
}

// --- Popover state ---

const chapterListOpen = ref(false)
const sleepMenuOpen = ref(false)
const bookmarkPanelOpen = ref(false)
const bookmarkLabelInputOpen = ref(false)
const bookmarkLabel = ref('')
const bookmarkLabelRef = ref<HTMLInputElement | null>(null)

// Refs for click-outside detection
const chapterWrapRef = ref<HTMLElement | null>(null)
const sleepWrapRef = ref<HTMLElement | null>(null)
const bookmarkWrapRef = ref<HTMLElement | null>(null)

function closeAllPopovers() {
  chapterListOpen.value = false
  sleepMenuOpen.value = false
  bookmarkPanelOpen.value = false
  bookmarkLabelInputOpen.value = false
}

function toggleChapterList() {
  const next = !chapterListOpen.value
  closeAllPopovers()
  chapterListOpen.value = next
}

function toggleSleepMenu() {
  const next = !sleepMenuOpen.value
  closeAllPopovers()
  sleepMenuOpen.value = next
}

function onBookmarkBtn() {
  const next = !bookmarkPanelOpen.value
  closeAllPopovers()
  bookmarkPanelOpen.value = next
  if (next && player.bookmarks.length === 0) {
    // Lazy-load bookmarks on first open
    void player.loadBookmarks()
  }
}

function handleGlobalClick(e: MouseEvent) {
  const target = e.target as Node
  const refs = [chapterWrapRef.value, sleepWrapRef.value, bookmarkWrapRef.value]
  for (const r of refs) {
    if (r && r.contains(target)) return
  }
  closeAllPopovers()
}

// --- Sleep timer helpers ---

function isSleepOptActive(val: SleepOpt): boolean {
  if (val === 'off') return player.sleepTimerMode === 'off'
  if (val === 'chapter') return player.sleepTimerMode === 'chapter'
  return player.sleepTimerMode === 'timed'
}

function setSleep(val: SleepOpt) {
  player.setSleepTimer(val)
  sleepMenuOpen.value = false
}

function formatSleepRemaining(): string {
  const s = player.sleepTimerRemainingSeconds()
  if (s <= 0) return ''
  const m = Math.floor(s / 60)
  const ss = s % 60
  return m > 0 ? `${m}m` : `${ss}s`
}

// --- Bookmark helpers ---

function addBookmark() {
  bookmarkLabelInputOpen.value = true
  bookmarkLabel.value = ''
  nextTick(() => bookmarkLabelRef.value?.focus())
}

async function confirmAddBookmark() {
  const label = bookmarkLabel.value.trim() || undefined
  bookmarkLabelInputOpen.value = false
  bookmarkLabel.value = ''
  await player.addBookmarkHere(label)
}

// --- Chapter navigation ---

function jumpToChapter(ch: Chapter) {
  player.skipToChapter(ch)
  chapterListOpen.value = false
  // If fileIndex didn't change, the _seekRequest watcher below handles the seek.
  // If fileIndex changed, the fileIndex watcher + onLoadedMetadata handles it.
}

// --- Audio element event handlers ---

function onLoadedMetadata() {
  if (!el.value) return
  el.value.currentTime = player.positionSeconds // resume from stored position
  el.value.playbackRate = player.rate
  el.value.volume = player.muted ? 0 : player.volume
  player.duration = el.value.duration
}

function onTimeUpdate() {
  if (!el.value) return
  player.positionSeconds = el.value.currentTime
  player.save() // store throttles writes to 10s
  // Check sleep timer
  if (player.checkSleepTrigger()) {
    doSleepFade()
  }
}

function onEnded() {
  player.onEnded()
  // If onEnded() advanced fileIndex, the fileIndex watcher triggers el.play().
  // If it didn't (last file), flush(true) was already called inside onEnded().
}

function onPause() {
  player.playing = false
  void player.flush()
}

// Fade audio.volume to 0 over ~5s then pause, and restore volume
function doSleepFade() {
  if (!el.value) return
  const audio = el.value
  const startVol = audio.volume
  const steps = 25
  const interval = 5000 / steps
  let step = 0
  const fade = setInterval(() => {
    step++
    if (!audio || step >= steps) {
      clearInterval(fade)
      audio.pause()
      // Restore volume for next play
      audio.volume = startVol
      return
    }
    audio.volume = startVol * (1 - step / steps)
  }, interval)
}

// --- User control handlers ---

function togglePlay() {
  if (!el.value) return
  if (player.playing) {
    el.value.pause()
  } else {
    void el.value.play().catch(() => {
      // Browser may block autoplay — user must interact with the player directly
    })
  }
}

function rewind10() {
  if (!el.value) return
  el.value.currentTime = Math.max(0, el.value.currentTime - 10)
}

function forward30() {
  if (!el.value) return
  el.value.currentTime += 30
}

function prevFile() {
  player.prevFile()
  // fileIndex watcher handles auto-play on the new file
}

function nextFile() {
  player.nextFile()
  // fileIndex watcher handles auto-play on the new file
}

function onScrubInput(e: Event) {
  const val = parseFloat((e.target as HTMLInputElement).value)
  if (el.value) el.value.currentTime = val
  // v-model.number already updated player.positionSeconds
}

function onRateChange(e: Event) {
  const val = parseFloat((e.target as HTMLSelectElement).value)
  player.setRate(val)
  if (el.value) el.value.playbackRate = val
}

function onVolumeInput(e: Event) {
  const val = parseFloat((e.target as HTMLInputElement).value)
  player.setVolume(val)
  if (player.muted && val > 0) player.muted = false
  if (el.value) el.value.volume = player.muted ? 0 : player.volume
}

async function onMarkFinished() {
  if (!player.finished) {
    await player.markFinished()
  }
}

// --- Watchers ---

// Auto-play when the active file changes (next/prev file, onEnded advancing to next)
watch(
  () => player.fileIndex,
  async () => {
    await nextTick()
    void el.value?.play().catch(() => {})
  },
)

// Respond to external play/pause commands (e.g. Play button in AudiobookDetailView)
watch(
  () => player.playing,
  async (shouldPlay) => {
    await nextTick()
    if (!el.value) return
    if (shouldPlay) {
      void el.value.play().catch(() => {})
    } else {
      el.value.pause()
    }
  },
  { flush: 'post' },
)

// Apply volume changes to the audio element
watch(
  () => [player.volume, player.muted] as const,
  ([vol, muted]) => {
    if (el.value) el.value.volume = muted ? 0 : vol
  },
)

// Programmatic seek requests (same-file chapter jumps, bookmark jumps)
watch(
  () => player._seekRequest,
  (req) => {
    if (req && el.value) {
      el.value.currentTime = req.position
    }
  },
  { deep: true },
)

// Update OS / lock-screen / headphone controls when book or file changes
watch(
  [() => player.current, () => player.fileIndex],
  () => { setupMediaSession() },
  { immediate: true },
)

function setupMediaSession() {
  if (!('mediaSession' in navigator)) return
  if (!player.current) return

  navigator.mediaSession.metadata = new MediaMetadata({
    title: player.current.title ?? '',
    artwork: coverUrl.value
      ? [{ src: coverUrl.value, sizes: '512x512', type: 'image/jpeg' }]
      : [],
  })

  navigator.mediaSession.setActionHandler('play', () => {
    void el.value?.play().catch(() => {})
  })
  navigator.mediaSession.setActionHandler('pause', () => {
    el.value?.pause()
  })
  navigator.mediaSession.setActionHandler('seekbackward', () => { rewind10() })
  navigator.mediaSession.setActionHandler('seekforward', () => { forward30() })
  navigator.mediaSession.setActionHandler('previoustrack', () => { prevFile() })
  navigator.mediaSession.setActionHandler('nexttrack', () => { nextFile() })
}

// --- Keyboard shortcuts (global, ignored when focus is in text fields) ---

function isInputFocused(): boolean {
  const tag = (document.activeElement as HTMLElement | null)?.tagName ?? ''
  const ce = (document.activeElement as HTMLElement | null)?.isContentEditable ?? false
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || ce
}

function onGlobalKeydown(e: KeyboardEvent) {
  if (!player.current) return
  if (isInputFocused()) return

  switch (e.key) {
    case ' ':
      e.preventDefault()
      togglePlay()
      break
    case 'ArrowLeft':
      e.preventDefault()
      rewind10()
      break
    case 'ArrowRight':
      e.preventDefault()
      forward30()
      break
    case 'ArrowUp':
      e.preventDefault()
      player.setVolume(Math.min(1, player.volume + 0.05))
      if (el.value) el.value.volume = player.muted ? 0 : player.volume
      break
    case 'ArrowDown':
      e.preventDefault()
      player.setVolume(Math.max(0, player.volume - 0.05))
      if (el.value) el.value.volume = player.muted ? 0 : player.volume
      break
    case 'b':
    case 'B':
      void player.addBookmarkHere()
      break
  }
}

// --- Lifecycle ---

function onBeforeUnload() {
  void player.flush()
}

onMounted(() => {
  window.addEventListener('beforeunload', onBeforeUnload)
  document.addEventListener('keydown', onGlobalKeydown)
  document.addEventListener('click', handleGlobalClick)

  // Apply stored volume to audio element on mount
  if (el.value) el.value.volume = player.muted ? 0 : player.volume

  // Handle the case where player.playing was set before this component mounted
  if (player.playing) {
    void nextTick(() => {
      void el.value?.play().catch(() => {})
    })
  }
})

onUnmounted(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  document.removeEventListener('keydown', onGlobalKeydown)
  document.removeEventListener('click', handleGlobalClick)
  void player.flush()
})
</script>

<style scoped>
/*
 * Design tokens used below — all defined in fe/src/styles/base/base.css:
 *   --bg-tertiary: #2a2a2a   (bar bg — matches nav/sidebar chrome)
 *   --bg-surface:  #3a3a3a   (button hover, borders)
 *   --bg-secondary: #1a1a1a  (popovers — darker for separation)
 *   --bg-primary:  #0f0f0f   (deepest dark — selects, inputs)
 *   --text-primary:   #ffffff (titles, dominant text)
 *   --text-secondary: #cccccc (icons, time, labels — 8.2:1 on bar bg)
 *   --brand-500: #2196f3     (accent / focus ring / scrub)
 *   --brand-600: #1976d2     (play btn hover)
 *   --brand-700: #0d47a1     (play btn active)
 *   --brand-300: #64b5f6     (chapter accent / active item — 5.9:1 on bar bg)
 *
 * Note: --text-muted is overridden to rgba(0,0,0,0.6) in light mode, so it is
 * NOT used here. Secondary labels use --text-secondary directly.
 */

/* --- Floating centered bar --- */
.audio-player {
  position: fixed;
  bottom: 14px;
  left: 0;
  right: 0;
  margin: 0 auto;
  max-width: 1040px;
  width: calc(100% - 24px);
  background-color: var(--bg-tertiary, #2a2a2a);
  border: 1px solid var(--bg-surface, #3a3a3a);
  border-radius: 12px;
  box-shadow:
    0 6px 32px rgba(0, 0, 0, 0.65),
    0 1px 6px rgba(0, 0, 0, 0.4);
  z-index: 900;
  display: flex;
  flex-direction: column;
  overflow: visible;
}

/* Progress row: elapsed time · scrub · total time, integrated into the bar body */
.player-progress {
  display: flex;
  align-items: center;
  gap: 0.625rem;
  padding: 10px 16px 2px;
  box-sizing: border-box;
}

.scrub-bar {
  flex: 1 1 auto;
  min-width: 0;
  height: 5px;
  cursor: pointer;
  accent-color: var(--brand-500, #2196f3);
  border-radius: 4px;
  /* ponytail: native range, no custom thumb lib needed */
}

.player-inner {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto minmax(max-content, 1fr);
  align-items: center;
  gap: 1.25rem;
  padding: 4px 16px 10px;
  box-sizing: border-box;
}

/* --- Left: cover + title + chapter + time --- */
.player-meta {
  display: flex;
  align-items: center;
  gap: 0.625rem;
  min-width: 0;
}

.player-cover {
  width: 44px;
  height: 44px;
  object-fit: cover;
  border-radius: 6px;
  flex-shrink: 0;
}

.player-info {
  min-width: 0;
}

.player-title {
  font-size: 0.875rem;
  font-weight: 600;
  color: var(--text-primary, #fff);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* Chapter name in brand-300 (#64b5f6): 5.9:1 on --bg-tertiary — passes AA */
.player-chapter {
  font-size: 0.72rem;
  color: var(--brand-300, #64b5f6);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  margin-top: 1px;
}

/* Time readout in --text-secondary (#cccccc): 8.2:1 on --bg-tertiary — passes AAA */
.player-time {
  font-size: 0.72rem;
  color: var(--text-secondary, #ccc);
  font-variant-numeric: tabular-nums;
  flex-shrink: 0;
  min-width: 52px;
  text-align: center;
}

/* --- Center: playback controls --- */
.player-controls-wrap {
  display: flex;
  flex-direction: column;
  align-items: center;
  flex-shrink: 0;
}

.player-controls {
  display: flex;
  align-items: center;
  gap: 0.4rem;
}

/* Base button — 36×36 meets ≥32px secondary target */
.player-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  padding: 0;
  font-size: 17px;
  /* --text-secondary (#cccccc) on --bg-tertiary (#2a2a2a) = 8.2:1 */
  color: var(--text-secondary, #ccc);
  border-radius: var(--radius-md, 6px);
  background: none;
  border: none;
  cursor: pointer;
  flex-shrink: 0;
  transition:
    background-color var(--transition-fast, 0.12s ease),
    color var(--transition-fast, 0.12s ease),
    transform 0.08s ease;
}

/* Play/pause: solid brand circle — visually dominant, 44×44 (≥40px primary target) */
.player-btn--play {
  width: 44px;
  height: 44px;
  border-radius: 50%;
  background-color: var(--brand-500, #2196f3);
  color: #fff;
  font-size: 22px;
}

/* Small utility buttons — 32×32 meets ≥32px minimum */
.player-btn--sm {
  width: 32px;
  height: 32px;
  font-size: 15px;
}

/* Hover: reveal surface tint; play keeps brand identity */
.player-btn:hover {
  color: var(--text-primary, #fff);
  background-color: var(--bg-surface, #3a3a3a);
}

.player-btn--play:hover {
  background-color: var(--brand-600, #1976d2);
  color: #fff;
}

/* Active/pressed: slight shrink gives tactile feedback */
.player-btn:active {
  transform: scale(0.93);
  opacity: 0.8;
}

.player-btn--play:active {
  background-color: var(--brand-700, #0d47a1);
  transform: scale(0.93);
  opacity: 1;
}

/* Focus ring: brand-500 (#2196f3) on --bg-tertiary = 5.2:1 — exceeds 3:1 for focus indicators */
.player-btn:focus-visible {
  outline: 2px solid var(--brand-500, #2196f3);
  outline-offset: 2px;
}

/* Active state (sleep/mark-done engaged) */
.player-btn--active {
  color: var(--brand-300, #64b5f6);
}

.player-btn--active:hover {
  color: var(--brand-300, #64b5f6);
}

/* --- Right: volume + extras --- */
.player-right {
  display: flex;
  align-items: center;
  gap: 8px;
  justify-content: flex-end;
  min-width: 0;
}

.volume-wrap {
  display: flex;
  align-items: center;
  gap: 2px;
}

.volume-slider {
  width: 56px;
  height: 4px;
  cursor: pointer;
  accent-color: var(--brand-500, #2196f3);
}

/* Speed selector */
.player-speed {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.player-speed-label {
  font-size: 0.72rem;
  color: var(--text-secondary, #ccc);
}

.speed-select {
  background: var(--bg-primary, #0f0f0f);
  border: 1px solid var(--bg-surface, #3a3a3a);
  /* --text-secondary (#cccccc) on --bg-primary (#0f0f0f) = ~12:1 */
  color: var(--text-secondary, #ccc);
  border-radius: var(--radius-sm, 4px);
  padding: 3px 6px;
  font-size: 0.85rem;
  cursor: pointer;
}

.speed-select:focus {
  outline: 2px solid var(--brand-500, #2196f3);
  outline-offset: 1px;
}

/* Sleep countdown badge inside the timer button */
.timer-badge {
  font-size: 0.6rem;
  font-variant-numeric: tabular-nums;
  margin-left: 1px;
  /* brand-300 (#64b5f6) on --bg-tertiary = 5.9:1 */
  color: var(--brand-300, #64b5f6);
  line-height: 1;
}

/* --- Popover container --- */
.popover-wrap {
  position: relative;
}

/* Shared popover panel — uses --bg-secondary (#1a1a1a) for clear separation from bar */
.player-popover {
  position: absolute;
  bottom: calc(100% + 10px);
  right: 0;
  background: var(--bg-secondary, #1a1a1a);
  border: 1px solid var(--bg-surface, #3a3a3a);
  border-radius: var(--radius-lg, 8px);
  box-shadow: 0 8px 28px rgba(0, 0, 0, 0.7);
  z-index: 950;
  min-width: 180px;
  max-width: 280px;
  overflow: hidden;
}

.chapter-popover {
  max-height: 280px;
  overflow-y: auto;
}

.sleep-popover {
  min-width: 140px;
}

.bookmark-popover {
  max-height: 280px;
  overflow-y: auto;
}

.popover-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 10px 6px;
  font-size: 0.75rem;
  font-weight: 600;
  /* --text-secondary (#cccccc) on --bg-secondary (#1a1a1a) = ~12:1 */
  color: var(--text-secondary, #ccc);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  border-bottom: 1px solid var(--bg-surface, #3a3a3a);
}

.popover-add-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  background: none;
  border: none;
  color: var(--text-secondary, #ccc);
  cursor: pointer;
  border-radius: var(--radius-sm, 4px);
  padding: 0;
  font-size: 14px;
  transition: background-color var(--transition-fast, 0.12s ease);
}

.popover-add-btn:hover {
  color: var(--text-primary, #fff);
  background: var(--bg-surface, #3a3a3a);
}

.popover-list {
  list-style: none;
  margin: 0;
  padding: 4px 0;
}

.popover-item {
  display: flex;
  align-items: center;
}

/* Active chapter/sleep option: brand-300 on --bg-secondary = 4.7:1 — passes AA */
.popover-item.active .popover-item-btn {
  color: var(--brand-300, #64b5f6);
  font-weight: 500;
}

.popover-item-btn {
  flex: 1 1 auto;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 10px;
  background: none;
  border: none;
  /* --text-primary (#fff) on --bg-secondary (#1a1a1a) = ~14.9:1 */
  color: var(--text-primary, #fff);
  cursor: pointer;
  text-align: left;
  font-size: 0.85rem;
  min-width: 0;
  transition: background-color var(--transition-fast, 0.12s ease);
}

.popover-item-btn:hover {
  background: var(--bg-tertiary, #2a2a2a);
}

.popover-item-label {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Secondary time labels: --text-secondary (#cccccc) on --bg-secondary = ~12:1 */
.popover-item-time {
  flex-shrink: 0;
  font-size: 0.72rem;
  color: var(--text-secondary, #ccc);
  font-variant-numeric: tabular-nums;
}

/* Delete button — 32×32 meets ≥32px minimum */
.popover-del-btn {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  background: none;
  border: none;
  color: var(--text-secondary, #ccc);
  cursor: pointer;
  border-radius: var(--radius-sm, 4px);
  font-size: 14px;
  padding: 0;
  transition: background-color var(--transition-fast, 0.12s ease);
}

.popover-del-btn:hover {
  color: var(--danger-500, #ff6b6b);
  background: rgba(255, 107, 107, 0.1);
}

.popover-del-btn:focus-visible {
  outline: 2px solid var(--brand-500, #2196f3);
  outline-offset: 1px;
}

.popover-empty {
  padding: 12px;
  text-align: center;
  font-size: 0.82rem;
  color: var(--text-secondary, #ccc);
}

.bookmark-label-wrap {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 6px 8px;
  border-bottom: 1px solid var(--bg-surface, #3a3a3a);
}

.bookmark-label-input {
  flex: 1 1 auto;
  background: var(--bg-primary, #0f0f0f);
  border: 1px solid var(--bg-surface, #3a3a3a);
  border-radius: var(--radius-sm, 4px);
  color: var(--text-primary, #fff);
  font-size: 0.85rem;
  padding: 5px 8px;
  outline: none;
}

.bookmark-label-input:focus {
  border-color: var(--brand-500, #2196f3);
}

/* --- Collapsed pill variant --- */
.audio-player--pill {
  max-width: 480px;
  border-radius: 32px;
  /* ponytail: fixed pill shape; pill width shrinks on narrow screens via width: calc(100% - 24px) on parent */
}

.pill-inner {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 10px 14px;
}

.pill-cover {
  width: 36px;
  height: 36px;
  border-radius: 6px;
  object-fit: cover;
  flex-shrink: 0;
}

.pill-title {
  flex: 1 1 auto;
  min-width: 0;
  font-size: 0.85rem;
  font-weight: 500;
  /* --text-primary (#fff) on --bg-tertiary = ~14:1 */
  color: var(--text-primary, #fff);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* --- Responsive: narrow screens --- */
@media (max-width: 700px) {
  .audio-player {
    bottom: 8px;
    width: calc(100% - 16px);
    border-radius: 10px;
  }

  .player-inner {
    grid-template-columns: auto minmax(0, 1fr);
    gap: 0.5rem;
    padding: 4px 8px 8px;
  }

  .player-right {
    display: none;
  }
}

@media (max-width: 480px) {
  .player-inner {
    grid-template-columns: auto 1fr;
    gap: 0.375rem;
  }

  .player-cover {
    width: 36px;
    height: 36px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .player-btn {
    transition: none;
  }

  .player-btn:active {
    transform: none;
  }
}
</style>
