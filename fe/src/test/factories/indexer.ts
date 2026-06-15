import type { Indexer } from '@/types'

export function createIndexer(overrides: Partial<Indexer> = {}): Indexer {
  return {
    id: 1,
    name: 'Test Indexer',
    type: 'Torrent',
    implementation: 'Torznab',
    url: 'https://indexer.example',
    apiKey: '',
    categories: '',
    animeCategories: '',
    tags: '',
    enableRss: true,
    enableAutomaticSearch: true,
    enableInteractiveSearch: true,
    enableAnimeStandardSearch: false,
    isEnabled: true,
    priority: 25,
    minimumAge: 0,
    retention: 0,
    maximumSize: 0,
    additionalSettings: '',
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  }
}
