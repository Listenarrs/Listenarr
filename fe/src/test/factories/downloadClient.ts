import type { DownloadClientConfiguration } from '@/types'

export function createDownloadClientConfiguration(
  overrides: Partial<DownloadClientConfiguration> = {},
): DownloadClientConfiguration {
  return {
    id: 'client-1',
    name: 'Test Client',
    type: 'qbittorrent',
    host: 'localhost',
    port: 8080,
    username: '',
    password: '',
    downloadPath: '',
    useSSL: false,
    isEnabled: true,
    settings: {},
    ...overrides,
  }
}
