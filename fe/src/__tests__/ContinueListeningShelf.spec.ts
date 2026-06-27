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
import { describe, it, expect, beforeEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { Audiobook } from '@/types'

// vi.hoisted ensures these are available when the vi.mock factory runs (which is hoisted to top)
const { mockGetContinueListening, mockGetPlayback } = vi.hoisted(() => ({
  mockGetContinueListening: vi.fn(async (): Promise<Audiobook[]> => []),
  mockGetPlayback: vi.fn(async () => ({
    audiobookId: 1,
    title: null,
    asin: null,
    files: [],
    fileIndex: 0,
    positionSeconds: 600,
    finished: false,
    chapters: [],
  })),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getContinueListening: mockGetContinueListening,
    getPlayback: mockGetPlayback,
    getImageUrl: vi.fn((url: string) => url || ''),
    savePlayback: vi.fn(async () => undefined),
  },
}))

vi.mock('vue-router', () => ({
  RouterLink: { template: '<a href="#"><slot /></a>' },
  useRouter: () => ({ push: vi.fn() }),
  useRoute: () => ({ params: {} }),
}))

// Minimal SignalR stub so the library store (imported transitively) doesn't error
vi.mock('@/services/signalr', () => ({
  signalRService: {
    onFilesRemoved: vi.fn(),
    onAudiobookUpdate: vi.fn(),
  },
}))

// Import after mocks are declared
import ContinueListeningShelf from '@/components/domain/audiobook/ContinueListeningShelf.vue'
import { usePlayerStore } from '@/stores/player'

const sampleBooks: Audiobook[] = [
  {
    id: 1,
    title: 'The Name of the Wind',
    authors: ['Patrick Rothfuss'],
    imageUrl: '/api/v1/images/ABC123',
    asin: 'ABC123',
    playbackPositionSeconds: 3600,
    runtime: 36000,
    monitored: true,
    finished: false,
  },
  {
    id: 2,
    title: 'Dune',
    authors: ['Frank Herbert'],
    imageUrl: '/api/v1/images/DEF456',
    asin: 'DEF456',
    playbackPositionSeconds: 1800,
    runtime: 0, // divide-by-zero guard
    monitored: true,
    finished: false,
  },
]

describe('ContinueListeningShelf', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
  })

  it('renders nothing when the list is empty', async () => {
    mockGetContinueListening.mockResolvedValueOnce([])
    const wrapper = mount(ContinueListeningShelf, { global: { plugins: [createPinia()] } })
    await flushPromises()
    expect(wrapper.find('.continue-listening-shelf').exists()).toBe(false)
  })

  it('renders a card per in-progress book', async () => {
    mockGetContinueListening.mockResolvedValueOnce(sampleBooks)
    const wrapper = mount(ContinueListeningShelf, { global: { plugins: [createPinia()] } })
    await flushPromises()
    expect(wrapper.findAll('.shelf-card')).toHaveLength(2)
    expect(wrapper.text()).toContain('The Name of the Wind')
    expect(wrapper.text()).toContain('Patrick Rothfuss')
  })

  it('shows the Continue Listening heading when there are books', async () => {
    mockGetContinueListening.mockResolvedValueOnce([sampleBooks[0]!])
    const wrapper = mount(ContinueListeningShelf, { global: { plugins: [createPinia()] } })
    await flushPromises()
    expect(wrapper.text()).toContain('Continue Listening')
  })

  it('calls player.load with the book id when Resume is clicked', async () => {
    mockGetContinueListening.mockResolvedValueOnce([sampleBooks[0]!])
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(ContinueListeningShelf, { global: { plugins: [pinia] } })
    await flushPromises()
    await wrapper.find('.resume-btn').trigger('click')
    await flushPromises()
    expect(mockGetPlayback).toHaveBeenCalledWith(1)
  })

  it('sets player.playing = true after Resume is clicked', async () => {
    mockGetContinueListening.mockResolvedValueOnce([sampleBooks[0]!])
    const pinia = createPinia()
    setActivePinia(pinia)
    const wrapper = mount(ContinueListeningShelf, { global: { plugins: [pinia] } })
    await flushPromises()
    const player = usePlayerStore()
    expect(player.playing).toBe(false)
    await wrapper.find('.resume-btn').trigger('click')
    await flushPromises()
    expect(player.playing).toBe(true)
  })

  it('clamps progress to 0 when runtime is 0 (divide-by-zero guard)', async () => {
    mockGetContinueListening.mockResolvedValueOnce([sampleBooks[1]!])
    const wrapper = mount(ContinueListeningShelf, { global: { plugins: [createPinia()] } })
    await flushPromises()
    const fill = wrapper.find('.progress-fill')
    expect(fill.attributes('style')).toContain('width: 0%')
  })

  it('computes progress correctly when runtime is valid', async () => {
    mockGetContinueListening.mockResolvedValueOnce([sampleBooks[0]!])
    const wrapper = mount(ContinueListeningShelf, { global: { plugins: [createPinia()] } })
    await flushPromises()
    // 3600 / 36000 = 10%
    const fill = wrapper.find('.progress-fill')
    expect(fill.attributes('style')).toContain('width: 10%')
  })

  it('remains hidden when the API call fails', async () => {
    mockGetContinueListening.mockRejectedValueOnce(new Error('network error'))
    const wrapper = mount(ContinueListeningShelf, { global: { plugins: [createPinia()] } })
    await flushPromises()
    expect(wrapper.find('.continue-listening-shelf').exists()).toBe(false)
  })
})
