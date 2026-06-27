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
  <section
    v-if="books.length > 0"
    class="continue-listening-shelf"
    aria-label="Continue Listening"
  >
    <h2 class="shelf-heading">Continue Listening</h2>
    <div class="shelf-row">
      <article v-for="book in books" :key="book.id" class="shelf-card">
        <router-link
          :to="`/audiobooks/${book.id}`"
          class="card-cover-link"
          :aria-label="`${book.title} — open detail`"
        >
          <div class="card-cover">
            <img
              :src="getProtectedImageSrc(coverUrl(book), '')"
              :alt="`Cover of ${book.title}`"
              class="cover-img"
              loading="lazy"
              decoding="async"
            />
          </div>
        </router-link>
        <div class="card-info">
          <router-link :to="`/audiobooks/${book.id}`" class="card-title">{{
            book.title
          }}</router-link>
          <p class="card-author">
            {{ (book.authors || []).join(', ') || 'Unknown Author' }}
          </p>
          <div
            class="progress-bar"
            role="progressbar"
            :aria-valuenow="progressPct(book)"
            aria-valuemin="0"
            aria-valuemax="100"
            :aria-label="`${progressPct(book)}% complete`"
          >
            <div class="progress-fill" :style="{ width: progressPct(book) + '%' }"></div>
          </div>
          <button class="resume-btn" @click="resume(book)">Resume</button>
        </div>
      </article>
    </div>
  </section>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { apiService } from '@/services/api'
import { usePlayerStore } from '@/stores/player'
import { buildApiPath } from '@/services/apiBase'
import { useProtectedImages } from '@/composables/useProtectedImages'
import type { Audiobook } from '@/types'

const books = ref<Audiobook[]>([])
const player = usePlayerStore()
const { getProtectedImageSrc } = useProtectedImages()

onMounted(async () => {
  try {
    books.value = await apiService.getContinueListening()
  } catch {
    // non-fatal: shelf stays hidden on error
  }
})

function coverUrl(book: Audiobook): string {
  const raw = (book.imageUrl || '').trim()
  const isPlaceholder =
    raw === '/placeholder.svg' || raw === 'placeholder.svg' || raw.endsWith('/placeholder.svg')
  if (raw && !isPlaceholder) return raw
  if (book.asin) return buildApiPath(`/images/${encodeURIComponent(book.asin)}`)
  return raw
}

function progressPct(book: Audiobook): number {
  const pos = book.playbackPositionSeconds ?? 0
  const rt = book.runtime ?? 0
  if (rt <= 0) return 0
  return Math.min(100, Math.max(0, Math.round((pos / rt) * 100)))
}

async function resume(book: Audiobook): Promise<void> {
  await player.load(book.id)
  player.playing = true
}
</script>

<style scoped>
.continue-listening-shelf {
  padding: 16px 20px 4px;
}

.shelf-heading {
  font-size: 1rem;
  font-weight: 600;
  color: var(--text-primary);
  margin: 0 0 12px;
}

.shelf-row {
  display: flex;
  gap: 12px;
  overflow-x: auto;
  padding-bottom: 8px;
  scrollbar-width: thin;
  scrollbar-color: var(--bg-surface) transparent;
}

.shelf-card {
  display: flex;
  flex-direction: column;
  flex: 0 0 120px;
  background: var(--bg-secondary);
  border-radius: 6px;
  overflow: hidden;
}

.card-cover-link {
  display: block;
  text-decoration: none;
}

.card-cover {
  width: 100%;
  aspect-ratio: 1;
  overflow: hidden;
  background: var(--bg-tertiary);
}

.cover-img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.card-info {
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.card-title {
  font-size: 0.75rem;
  font-weight: 600;
  color: var(--text-primary);
  text-decoration: none;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  line-height: 1.3;
}

.card-title:hover {
  color: var(--brand-500);
}

.card-author {
  font-size: 0.7rem;
  color: var(--text-secondary);
  margin: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.progress-bar {
  height: 3px;
  background: var(--bg-surface);
  border-radius: 2px;
  overflow: hidden;
}

.progress-fill {
  height: 100%;
  background: var(--brand-500);
  border-radius: 2px;
  transition: width 0.3s ease;
}

.resume-btn {
  margin-top: 4px;
  padding: 4px 8px;
  font-size: 0.7rem;
  font-weight: 600;
  color: var(--bg-primary);
  background: var(--brand-500);
  border: none;
  border-radius: 4px;
  cursor: pointer;
  width: 100%;
  transition: opacity 0.15s ease;
}

.resume-btn:hover {
  opacity: 0.85;
}

.resume-btn:focus-visible {
  outline: 2px solid var(--brand-500);
  outline-offset: 2px;
}
</style>
