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
import { onBeforeUnmount, ref } from 'vue'
import { revealMs } from '@/utils/resultsReveal'

// Results just answered, shown under an overlay fading out ("Found 3 results"): not pickable
// until it is gone (utils/resultsReveal holds its timing)
export function useResultsReveal() {
  const isRevealing = ref(false)
  let revealTimer: ReturnType<typeof setTimeout> | null = null

  function start() {
    end()
    if (revealMs() <= 0) return
    isRevealing.value = true
    revealTimer = setTimeout(end, revealMs())
  }

  function end() {
    if (revealTimer) clearTimeout(revealTimer)
    revealTimer = null
    isRevealing.value = false
  }

  onBeforeUnmount(end)

  return { isRevealing, start, end }
}
