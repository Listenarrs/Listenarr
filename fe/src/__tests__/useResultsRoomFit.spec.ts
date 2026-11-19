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
import { mount, flushPromises, enableAutoUnmount } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import { afterEach, describe, expect, it } from 'vitest'
import { useResultsRoomFit } from '@/composables/useResultsRoomFit'

enableAutoUnmount(afterEach)

// A dialog body whose content is that much taller than what it shows (jsdom has no layout)
function bodyOverflowing(overflow: number) {
  const body = document.createElement('div')
  Object.defineProperty(body, 'clientHeight', { value: 648 })
  Object.defineProperty(body, 'scrollHeight', { value: 648 + overflow })
  return body
}

function fitted(body: HTMLElement, isFitted = ref(true)) {
  let state!: ReturnType<typeof useResultsRoomFit>
  mount(
    defineComponent({
      setup() {
        state = useResultsRoomFit(
          () => body,
          () => isFitted.value,
        )
        return () => h('div')
      },
    }),
  )
  return { state, isFitted }
}

describe('useResultsRoomFit', () => {
  it('takes what the body overflows off the results room', async () => {
    const { state } = fitted(bodyOverflowing(100))
    await flushPromises()
    expect(state.room.value).toBe(220)
    expect(state.roomStyle.value).toEqual({ '--match-results-room': '220px' })
  })

  it('keeps at least two results of room (the dialog scrolls past that)', async () => {
    const { state } = fitted(bodyOverflowing(400))
    await flushPromises()
    expect(state.room.value).toBe(150)
  })

  it('keeps the full room when nothing overflows', async () => {
    const { state } = fitted(bodyOverflowing(0))
    await flushPromises()
    expect(state.room.value).toBeNull()
    expect(state.roomStyle.value).toEqual({})
  })

  it('fits only when asked, and again when the window is resized', async () => {
    let overflow = 100
    const body = document.createElement('div')
    Object.defineProperty(body, 'clientHeight', { get: () => 648 })
    Object.defineProperty(body, 'scrollHeight', { get: () => 648 + overflow })
    const { state, isFitted } = fitted(body, ref(false))
    await flushPromises()
    expect(state.room.value).toBeNull()

    isFitted.value = true
    await flushPromises()
    expect(state.room.value).toBe(220)

    overflow = 50
    window.dispatchEvent(new Event('resize'))
    await flushPromises()
    expect(state.room.value).toBe(270)
  })
})
