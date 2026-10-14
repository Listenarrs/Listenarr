import { describe, it, expect, beforeEach, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'

vi.mock('@/services/api', () => ({
  apiService: {
    getSearchActivity: vi.fn().mockResolvedValue({
      current: { message: 'Search idle', stage: 'idle', timestamp: '2026-06-13T00:00:00Z' },
      recent: [
        {
          message: 'Grabbed 1 for A',
          stage: 'grabbed',
          audiobookId: 7,
          timestamp: '2026-06-13T00:00:01Z',
        },
      ],
    }),
  },
}))

vi.mock('@/services/signalr', () => ({
  signalRService: { onSearchProgress: vi.fn(() => () => {}) },
}))

vi.mock('@/utils/logger', () => ({ logger: { debug: vi.fn() } }))

import { useSearchActivityStore } from '@/stores/searchActivity'

describe('searchActivity store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('hydrates current + recent from the API', async () => {
    const store = useSearchActivityStore()
    await store.hydrate()
    expect(store.current?.stage).toBe('idle')
    expect(store.recent).toHaveLength(1)
    expect(store.recent[0].audiobookId).toBe(7)
  })

  it('isSearching reflects the searching stage', () => {
    const store = useSearchActivityStore()
    store.applyEvent({ message: 'Searching 13 indexers for X', stage: 'searching' })
    expect(store.isSearching).toBe(true)
    store.applyEvent({ message: 'No results for X', stage: 'no_results' })
    expect(store.isSearching).toBe(false)
  })

  it('appends only outcomes to the feed (newest first), not transient stages', () => {
    const store = useSearchActivityStore()
    store.applyEvent({ message: 'Searching for X', stage: 'searching' })
    expect(store.recent).toHaveLength(0)
    store.applyEvent({ message: 'Grabbed 1 for X', stage: 'grabbed', audiobookId: 1 })
    store.applyEvent({ message: 'No results for Y', stage: 'no_results', audiobookId: 2 })
    expect(store.recent.map((e) => e.message)).toEqual(['No results for Y', 'Grabbed 1 for X'])
  })
})
