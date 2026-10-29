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
import { createPinia, setActivePinia } from 'pinia'
import { effectScope } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SearchResult } from '@/types'

const apiMocks = vi.hoisted(() => ({
  getAudiobook: vi.fn(),
  getAudiobookIdentifiers: vi.fn(),
  updateAudiobookIdentifiers: vi.fn(),
  updateAudiobook: vi.fn(),
  rescanAudiobookMetadata: vi.fn(),
  getApplicationSettings: vi.fn(),
}))
vi.mock('@/services/api', () => ({ apiService: apiMocks }))
vi.mock('@/utils/logger', () => ({ logger: { error: vi.fn(), debug: vi.fn(), warn: vi.fn() } }))

import { useRepairBooks } from '@/composables/useRepairBooks'

const primalHunter4 = {
  asin: 'B0BK9W82PQ',
  title: 'Primal Hunter 4',
  authors: [{ name: 'Zogarth' }],
  region: 'us',
} as unknown as SearchResult

// The composable on its own, in a scope like a component's, its books loaded and picked
async function repairBooksPicked(audiobookIds: number[], picked: SearchResult) {
  const scope = effectScope()
  const state = scope.run(() => useRepairBooks(() => audiobookIds))!
  await state.load()
  for (const repairRow of state.repairRows.value) {
    repairRow.bookMatch.pickedSearchResult = picked
    repairRow.isSelected = true
  }
  return state
}

describe("useRepairBooks: an error outside a book's own repair", () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setActivePinia(createPinia())
    apiMocks.getApplicationSettings.mockResolvedValue({ defaultSearchRegion: 'fr' })
    apiMocks.getAudiobook.mockImplementation(async (audiobookId: number) => ({
      id: audiobookId,
      title: `Book ${audiobookId}`,
      authors: ['Author'],
    }))
    apiMocks.getAudiobookIdentifiers.mockResolvedValue({
      identifiers: [
        { type: 'Asin', value: 'B0HM45F8MW', region: 'fr', isPrimary: true, source: 'Imported' },
      ],
    })
    apiMocks.updateAudiobookIdentifiers.mockResolvedValue({ message: 'ok', identifiers: [] })
    apiMocks.updateAudiobook.mockResolvedValue({ message: 'ok' })
    apiMocks.rescanAudiobookMetadata.mockResolvedValue({
      message: 'ok',
      audiobookId: 1,
      asin: 'B0BK9W82PQ',
      region: 'us',
    })
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('ends the run, the books failed and shown, when a picked result is broken', async () => {
    // A provider's result with a broken author entry: building its planned record throws
    const state = await repairBooksPicked([1, 2], {
      ...primalHunter4,
      authors: [null],
    } as unknown as SearchResult)

    await state.repair(async () => {})

    expect(state.isExecuting.value).toBe(false)
    expect(state.isFinished.value).toBe(true)
    expect(apiMocks.rescanAudiobookMetadata).not.toHaveBeenCalled()
    // Both on step 2, failed, saying why
    expect(state.repairOutcomes.value.map((outcome) => outcome.state)).toEqual(['failed', 'failed'])
    expect(state.repairOutcomes.value[0]!.message).toMatch(/^Repair stopped: /)
  })

  it('ends the run when what runs before the repairs fails', async () => {
    const state = await repairBooksPicked([1], primalHunter4)

    await state.repair(async () => {
      throw new Error('Layout measure failed')
    })

    expect(state.isExecuting.value).toBe(false)
    expect(state.repairOutcomes.value[0]).toMatchObject({
      state: 'failed',
      message: 'Repair stopped: Layout measure failed',
    })
  })

  it("ends a Retry that fails outside the book's own repair", async () => {
    apiMocks.rescanAudiobookMetadata.mockRejectedValueOnce(new Error('Audible is down'))
    const state = await repairBooksPicked([1], primalHunter4)
    await state.repair(async () => {})
    expect(state.repairOutcomes.value[0]!.state).toBe('warning')

    // The clock itself fails as the Retry starts
    vi.spyOn(performance, 'now').mockImplementationOnce(() => {
      throw new Error('Clock failed')
    })
    await state.retryRepair(1)

    expect(state.isExecuting.value).toBe(false)
    expect(state.repairOutcomes.value[0]).toMatchObject({
      state: 'failed',
      message: 'Repair stopped: Clock failed',
    })
  })
})
