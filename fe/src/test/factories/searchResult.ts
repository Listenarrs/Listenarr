import type { SearchResult } from '@/types'

export function createSearchResult(
  overrides: Partial<SearchResult> & Record<string, unknown> = {},
): SearchResult {
  return {
    id: 'result-1',
    title: 'Test Result',
    artist: 'Test Author',
    album: 'Test Result',
    category: 'Audiobook',
    source: 'Test Indexer',
    publishedDate: '2026-01-01T00:00:00.000Z',
    format: 'MP3',
    author: 'Test Author',
    authors: ['Test Author'],
    narrators: [],
    asin: 'B000000001',
    size: 0,
    magnetLink: '',
    torrentUrl: '',
    nzbUrl: '',
    downloadType: 'Torrent',
    quality: 'MP3',
    imageUrl: '',
    metadataSource: 'Audible',
    ...overrides,
  } as SearchResult
}
