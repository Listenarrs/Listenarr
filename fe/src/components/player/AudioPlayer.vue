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
  <div v-if="player.current" class="audio-player" role="region" aria-label="Audio player">
    <!-- Hidden audio element — the actual playback engine -->
    <audio
      ref="el"
      :src="src"
      @loadedmetadata="onLoadedMetadata"
      @timeupdate="onTimeUpdate"
      @ended="onEnded"
      @play="player.playing = true"
      @pause="onPause"
    />

    <div class="player-inner">
      <!-- Cover thumbnail + title + time -->
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
          <div class="player-time">
            {{ formatTime(player.positionSeconds) }} / {{ formatTime(player.duration) }}
          </div>
        </div>
      </div>

      <!-- Scrub bar + playback controls -->
      <div class="player-controls-wrap">
        <input
          type="range"
          class="scrub-bar"
          min="0"
          :max="player.duration || 0"
          v-model.number="player.positionSeconds"
          @input="onScrubInput"
          aria-label="Playback position"
        />
        <div class="player-controls" role="group" aria-label="Playback controls">
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
        </div>
      </div>

      <!-- Speed selector -->
      <div class="player-right">
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
    </div>
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
import { PhPlay, PhPause, PhSkipBack, PhSkipForward, PhRewind, PhFastForward } from '@phosphor-icons/vue'
import { usePlayerStore } from '@/stores/player'
import { apiService } from '@/services/api'
import { buildApiPath } from '@/services/apiBase'

const SPEEDS = [0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3]

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

// --- Audio element event handlers ---

function onLoadedMetadata() {
  if (!el.value) return
  el.value.currentTime = player.positionSeconds // resume from stored position
  el.value.playbackRate = player.rate
  player.duration = el.value.duration
}

function onTimeUpdate() {
  if (!el.value) return
  player.positionSeconds = el.value.currentTime
  player.save() // store throttles writes to 10s
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

// --- Lifecycle ---

function onBeforeUnload() {
  void player.flush()
}

onMounted(() => {
  window.addEventListener('beforeunload', onBeforeUnload)
  // Handle the case where player.playing was set before this component mounted
  // (e.g. detail view sets playing=true, then Vue renders AudioPlayer)
  if (player.playing) {
    void nextTick(() => {
      void el.value?.play().catch(() => {})
    })
  }
})

onUnmounted(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  void player.flush()
})
</script>

<style scoped>
.audio-player {
  position: fixed;
  bottom: 0;
  left: 0;
  right: 0;
  height: 72px;
  background-color: #2a2a2a;
  border-top: 1px solid #3a3a3a;
  z-index: 900;
  display: flex;
  align-items: center;
}

.player-inner {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 2fr) auto;
  align-items: center;
  gap: 1rem;
  width: 100%;
  padding: 0 1rem;
  box-sizing: border-box;
  height: 100%;
}

/* Left: cover thumbnail + title + position */
.player-meta {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  min-width: 0;
}

.player-cover {
  width: 48px;
  height: 48px;
  object-fit: cover;
  border-radius: 4px;
  flex-shrink: 0;
}

.player-info {
  min-width: 0;
}

.player-title {
  font-size: 0.875rem;
  font-weight: 500;
  color: #fff;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.player-time {
  font-size: 0.72rem;
  color: #9aa0a6;
  margin-top: 2px;
  font-variant-numeric: tabular-nums;
}

/* Center: scrub bar stacked above controls */
.player-controls-wrap {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  min-width: 0;
  width: 100%;
}

.scrub-bar {
  width: 100%;
  height: 4px;
  cursor: pointer;
  accent-color: var(--brand-500, #2196f3);
  /* ponytail: native range, no custom thumb lib needed */
}

.player-controls {
  display: flex;
  align-items: center;
  gap: 0.125rem;
}

.player-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 34px;
  height: 34px;
  padding: 0;
  font-size: 16px;
  color: #bbb;
  border-radius: 6px;
}

.player-btn--play {
  width: 40px;
  height: 40px;
  color: #fff;
  font-size: 22px;
}

.player-btn:hover {
  color: #fff;
  background-color: #3a3a3a;
}

.player-btn:focus-visible {
  outline: 2px solid var(--brand-500, #2196f3);
  outline-offset: 1px;
}

/* Right: speed selector */
.player-right {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.player-speed-label {
  font-size: 0.72rem;
  color: #9aa0a6;
}

.speed-select {
  background: #1a1a1a;
  border: 1px solid #3a3a3a;
  color: #ccc;
  border-radius: 4px;
  padding: 3px 6px;
  font-size: 0.85rem;
  cursor: pointer;
}

.speed-select:focus {
  outline: 2px solid var(--brand-500, #2196f3);
  outline-offset: 1px;
}

/* Responsive: tighten on small screens */
@media (max-width: 768px) {
  .audio-player {
    height: 64px;
  }

  .player-inner {
    grid-template-columns: auto minmax(0, 1fr) auto;
    gap: 0.5rem;
    padding: 0 0.5rem;
  }

  .player-cover {
    width: 40px;
    height: 40px;
  }

  .player-speed-label {
    display: none;
  }
}

@media (max-width: 480px) {
  .player-inner {
    grid-template-columns: auto minmax(0, 1fr);
  }

  .player-right {
    display: none;
  }
}

@media (prefers-reduced-motion: reduce) {
  .scrub-bar {
    transition: none;
  }
}
</style>
