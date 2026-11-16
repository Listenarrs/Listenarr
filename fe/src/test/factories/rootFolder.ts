import type { RootFolder } from '@/types'

export function createRootFolder(overrides: Partial<RootFolder> = {}): RootFolder {
  return {
    id: 1,
    path: 'C:\\Books',
    name: 'Books',
    ...overrides,
  } as RootFolder
}
