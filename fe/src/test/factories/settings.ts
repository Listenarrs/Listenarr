import type { ApplicationSettings } from '@/types'

export function createApplicationSettings(
  overrides: Partial<ApplicationSettings> = {},
): ApplicationSettings {
  return {
    outputPath: 'C:\\Books',
    ...overrides,
  } as ApplicationSettings
}
