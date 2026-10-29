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
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'

// The room a book card keeps for its results when it is alone in its dialog (Find Match, Repair
// with one book): its full height, less what the dialog's body would overflow, so the dialog never
// scrolls; at least two results high (the list scrolls inside past what fits). Set as the CSS
// variable the card's results read, measured when the card shows and when the window is resized,
// before any search answers, so nothing moves once results arrive.
export const RESULTS_ROOM_PX = 320
export const MIN_RESULTS_ROOM_PX = 150

export function useResultsRoomFit(
  bodyElement: () => HTMLElement | null | undefined,
  isFitted: () => boolean,
) {
  const room = ref<number | null>(null)

  async function fit() {
    // Measured with the full room first
    room.value = null
    if (!isFitted()) return
    await nextTick()
    const body = bodyElement()
    if (!body) return
    const overflow = body.scrollHeight - body.clientHeight
    room.value = overflow > 0 ? Math.max(MIN_RESULTS_ROOM_PX, RESULTS_ROOM_PX - overflow) : null
  }

  function onResize() {
    void fit()
  }

  watch(isFitted, () => void fit())
  onMounted(() => {
    window.addEventListener('resize', onResize)
    void fit()
  })
  onBeforeUnmount(() => window.removeEventListener('resize', onResize))

  const roomStyle = computed(() => {
    const style: Record<string, string> = {}
    if (room.value !== null) style['--match-results-room'] = `${room.value}px`
    return style
  })
  return { room, roomStyle, fit }
}
