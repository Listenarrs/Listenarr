import { vi } from 'vitest'

type ApiMockOverrides = Record<string, unknown>

export function createApiServiceMock<TOverrides extends ApiMockOverrides = ApiMockOverrides>(
  overrides: TOverrides = {} as TOverrides,
) {
  const apiService = {
    searchAudibleByTitleAndAuthor: vi.fn(async () => ({ totalResults: 0, results: [] })),
    advancedSearch: vi.fn(async () => ({ totalResults: 0, results: [] })),
    getImageUrl: vi.fn((url: string) => url || ''),
    getStartupConfig: vi.fn(async () => ({})),
    getApplicationSettings: vi.fn(async () => ({})),
    getLibrary: vi.fn(async () => []),
    previewLibraryPath: vi.fn(async () => ({ fullPath: '', relativePath: '' })),
    previewRename: vi.fn(async () => []),
    executeRename: vi.fn(async () => []),
    getQualityProfiles: vi.fn(async () => []),
    getApiConfigurations: vi.fn(async () => []),
    getRootFolders: vi.fn(async () => []),
    checkVolume: vi.fn(async () => ({ sameVolume: true, willBreakHardlinks: false })),
    ...overrides,
  }

  return apiService as typeof apiService & TOverrides
}

export function createApiModuleMock<TOverrides extends ApiMockOverrides = ApiMockOverrides>(
  overrides: TOverrides = {} as TOverrides,
) {
  const apiService = createApiServiceMock(overrides)

  return {
    apiService,
    getRemotePathMappings: vi.fn(async () => []),
    testDownloadClient: vi.fn(async () => ({ success: true, message: 'ok' })),
    ensureImageCached: vi.fn(async (url: string) => url || ''),
    getLogs: vi.fn(async () => []),
    downloadLogs: vi.fn(async () => null),
    getRootFolders: vi.fn(async () => []),
    getQualityProfiles: vi.fn(async () => []),
    getStartupConfig: vi.fn(async () => ({})),
    getApplicationSettings: vi.fn(async () => ({})),
    checkVolume: vi.fn(async () => ({ sameVolume: true, willBreakHardlinks: false })),
  }
}
