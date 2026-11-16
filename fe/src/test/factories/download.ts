import type { Download } from '@/types'

export function createDownload(overrides: Partial<Download> = {}): Download {
  return {
    id: 'download-1',
    title: 'Test Download',
    status: 'Downloading',
    downloadClientId: 'client-1',
    ...overrides,
  } as Download
}
