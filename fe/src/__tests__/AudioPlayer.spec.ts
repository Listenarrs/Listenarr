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
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { describe, it, beforeEach, expect, vi } from 'vitest'
import type { PlaybackState } from '@/types'

// jsdom does not implement HTMLMediaElement play/pause/currentTime — stub them
const playStub = vi.fn().mockResolvedValue(undefined)
const pauseStub = vi.fn()

Object.defineProperty(HTMLMediaElement.prototype, 'play', {
  configurable: true,
  value: playStub,
})
Object.defineProperty(HTMLMediaElement.prototype, 'pause', {
  configurable: true,
  value: pauseStub,
})
Object.defineProperty(HTMLMediaElement.prototype, 'currentTime', {
  configurable: true,
  get: vi.fn().mockReturnValue(0),
  set: vi.fn(),
})

vi.mock('@/services/api', () => ({
  apiService: {
    streamUrl: (id: number, idx: number) => `/audiobooks/${id}/files/${idx}/stream`,
    savePlayback: vi.fn().mockResolvedValue(undefined),
  },
}))

vi.mock('@/services/apiBase', () => ({
  buildApiPath: (path: string) => `/api${path}`,
}))

// Import after mocks are registered
import { usePlayerStore } from '@/stores/player'
import AudioPlayer from '@/components/player/AudioPlayer.vue'

function makeState(overrides: Partial<PlaybackState> = {}): PlaybackState {
  return {
    audiobookId: 1,
    title: 'Test Audiobook',
    asin: 'B0TEST001',
    files: [{ index: 0, durationSeconds: 3600, contentType: 'audio/mpeg' }],
    fileIndex: 0,
    positionSeconds: 0,
    finished: false,
    ...overrides,
  }
}

describe('AudioPlayer', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    playStub.mockClear()
    pauseStub.mockClear()
  })

  it('renders nothing when player.current is null', () => {
    const wrapper = mount(AudioPlayer)
    expect(wrapper.find('.audio-player').exists()).toBe(false)
    expect(wrapper.find('audio').exists()).toBe(false)
  })

  it('renders the player and audio element when player.current is set', () => {
    const store = usePlayerStore()
    store.current = makeState()

    const wrapper = mount(AudioPlayer)

    expect(wrapper.find('.audio-player').exists()).toBe(true)
    expect(wrapper.find('audio').exists()).toBe(true)
  })

  it('displays the book title', () => {
    const store = usePlayerStore()
    store.current = makeState({ title: 'My Audiobook' })

    const wrapper = mount(AudioPlayer)

    expect(wrapper.find('.player-title').text()).toBe('My Audiobook')
  })

  it('sets the audio src from apiService.streamUrl', () => {
    const store = usePlayerStore()
    store.current = makeState({ audiobookId: 7 })
    store.fileIndex = 2

    const wrapper = mount(AudioPlayer)
    const audio = wrapper.find('audio')

    expect(audio.attributes('src')).toBe('/audiobooks/7/files/2/stream')
  })

  it('shows Play button when not playing, Pause when playing', async () => {
    const store = usePlayerStore()
    store.current = makeState()
    store.playing = false

    const wrapper = mount(AudioPlayer)
    expect(wrapper.find('[aria-label="Play"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="Pause"]').exists()).toBe(false)

    store.playing = true
    await wrapper.vm.$nextTick()

    expect(wrapper.find('[aria-label="Pause"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="Play"]').exists()).toBe(false)
  })

  it('clicking the Play button calls play() on the audio element', async () => {
    const store = usePlayerStore()
    store.current = makeState()
    store.playing = false

    const wrapper = mount(AudioPlayer, { attachTo: document.body })
    await wrapper.vm.$nextTick()

    const playBtn = wrapper.find('[aria-label="Play"]')
    expect(playBtn.exists()).toBe(true)
    await playBtn.trigger('click')

    expect(playStub).toHaveBeenCalled()
    wrapper.unmount()
  })

  it('has a scrub bar input with correct max bound to player.duration', () => {
    const store = usePlayerStore()
    store.current = makeState()
    store.duration = 3600

    const wrapper = mount(AudioPlayer)
    const scrub = wrapper.find('.scrub-bar')

    expect(scrub.exists()).toBe(true)
    expect(scrub.attributes('max')).toBe('3600')
    expect(scrub.attributes('aria-label')).toBe('Playback position')
  })

  it('renders speed options matching the allowed speed list', () => {
    const store = usePlayerStore()
    store.current = makeState()

    const wrapper = mount(AudioPlayer)
    const options = wrapper.findAll('.speed-select option')
    const values = options.map((o) => parseFloat(o.element.value))

    expect(values).toEqual([0.75, 1, 1.25, 1.5, 1.75, 2, 2.5, 3])
  })
})
