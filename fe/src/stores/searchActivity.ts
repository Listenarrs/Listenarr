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
import type { SearchActivityEvent } from '@/types'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { logger } from '@/utils/logger'

// Mirrors the backend ring-buffer cap; trims the live-appended feed so it can't
// grow without bound between hydrations.
const MAX_RECENT = 30

/**
 * Ambient view of what the background automatic-search sweep is doing. Hydrates
 * once from /search/activity (so a freshly-loaded page shows state immediately,
 * even if the sweep is idle and the next SignalR event is hours away), then
 * stays live via the `SearchProgress` channel with `includeAutomatic`.
 */
export const useSearchActivityStore = defineStore('searchActivity', () => {
  const current = ref<SearchActivityEvent | null>(null)
  const recent = ref<SearchActivityEvent[]>([])
  const hydrated = ref(false)

  let unsubscribe: (() => void) | null = null

  // True while a book is actively being queried — drives the pulsing indicator.
  const isSearching = computed(() => current.value?.stage === 'searching')

  function applyEvent(payload: {
    message: string
    stage?: string
    audiobookId?: number
    asin?: string | null
    timestamp?: string
  }) {
    const evt: SearchActivityEvent = {
      message: payload.message,
      stage: (payload.stage as SearchActivityEvent['stage']) || 'searching',
      audiobookId: payload.audiobookId ?? null,
      asin: payload.asin ?? null,
      timestamp: payload.timestamp ?? new Date().toISOString(),
    }
    current.value = evt
    // Outcomes form the feed; transient stages (searching/idle) only update the
    // live line — matching the backend's addToFeed semantics.
    if (evt.stage === 'grabbed' || evt.stage === 'no_results') {
      recent.value = [evt, ...recent.value].slice(0, MAX_RECENT)
    }
  }

  async function hydrate() {
    try {
      const snapshot = await apiService.getSearchActivity()
      current.value = snapshot.current
      recent.value = snapshot.recent ?? []
    } catch (err) {
      logger.debug('Failed to hydrate search activity', err)
    } finally {
      hydrated.value = true
    }
  }

  /**
   * Idempotent: hydrate once and attach the live subscription. Defensive — a
   * not-yet-ready or partially-stubbed SignalR service must never break the app
   * shell that mounts the indicator.
   */
  function start() {
    if (!hydrated.value) {
      void hydrate()
    }
    if (!unsubscribe && typeof signalRService.onSearchProgress === 'function') {
      try {
        unsubscribe = signalRService.onSearchProgress((payload) => {
          if (payload.type && payload.type !== 'automatic') return
          applyEvent(payload)
        }, true)
      } catch (err) {
        logger.debug('Failed to subscribe to search activity', err)
      }
    }
  }

  function stop() {
    unsubscribe?.()
    unsubscribe = null
  }

  return { current, recent, hydrated, isSearching, hydrate, start, stop, applyEvent }
})
