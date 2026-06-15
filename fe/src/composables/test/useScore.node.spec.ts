/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { describe, it, expect } from 'vitest'
import { getScoreBreakdownTooltip } from '@/composables/useScore'
import type { QualityScore } from '@/types'
import { createSearchResult } from '@/test/factories/searchResult'

describe('useScore composable', () => {
  it('includes Smart composite breakdown when provided', () => {
    const score: QualityScore = {
      searchResult: createSearchResult({ id: 'r1', title: 'T' }),
      totalScore: 100,
      scoreBreakdown: { Quality: 90 },
      rejectionReasons: [],
      isRejected: false,
      smartScore: 1234.5,
      smartScoreBreakdown: { Quality: 90000, Format: 8500, Seed: 2000 },
    }

    const tooltip = getScoreBreakdownTooltip(score)
    expect(tooltip).toContain('Smart (composite) breakdown:')
    // Normalized quality should appear (90000 -> 90 when divided by 1000)
    expect(tooltip).toContain('Quality: +90')
    // Smart total now is the average of normalized components: Quality=90, Format=85, Seed=20 -> avg=~65
    expect(tooltip).toContain('Smart Total: 65')
  })
})
