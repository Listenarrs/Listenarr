import type { Audiobook } from '@/types'

export function createAudiobook(overrides: Partial<Audiobook> = {}): Audiobook {
  return {
    id: 1,
    title: 'Test Book',
    authors: ['Test Author'],
    narrators: [],
    files: [],
    monitored: true,
    tags: [],
    ...overrides,
  } as Audiobook
}
