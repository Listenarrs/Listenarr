import type { QualityProfile } from '@/types'

export function createQualityProfile(overrides: Partial<QualityProfile> = {}): QualityProfile {
  return {
    id: 1,
    name: 'Any',
    cutoff: 'Any',
    allowedFormats: [],
    ...overrides,
  } as QualityProfile
}
