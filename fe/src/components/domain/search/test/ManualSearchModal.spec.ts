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
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { describe, it, expect, vi, afterEach } from 'vitest'
import ManualSearchModal from '@/components/domain/search/ManualSearchModal.vue'
import * as apiModule from '@/services/api'
import { modalStubs } from '@/test/stubs'
import { delay } from '@/test/utils/wait'

const { apiService } = apiModule

if (!(apiService as any as Record<string, unknown>).getEnabledIndexers) {
  ;(apiService as any as { getEnabledIndexers: () => Promise<unknown[]> }).getEnabledIndexers =
    async () => []
}
if (!(apiService as any as Record<string, unknown>).searchByApi) {
  ;(apiService as any as { searchByApi: () => Promise<unknown[]> }).searchByApi = async () => []
}
if (!(apiService as any as Record<string, unknown>).getDefaultQualityProfile) {
  ;(
    apiService as any as {
      getDefaultQualityProfile: () => Promise<{ id: number }>
    }
  ).getDefaultQualityProfile = async () => ({ id: 1 })
}
if (!(apiService as any as Record<string, unknown>).scoreSearchResults) {
  ;(
    apiService as any as {
      scoreSearchResults: () => Promise<unknown[]>
    }
  ).scoreSearchResults = async () => []
}

type ManualSearchResult = {
  id: string
  title?: string
  downloadType?: string
  resultUrl?: string
  source?: string
  nzbUrl?: string
  sourceLink?: string
  size?: number
  quality?: string
  format?: string
  language?: string
}

type QualityScore = {
  searchResult: ManualSearchResult
  totalScore: number
  scoreBreakdown: Record<string, unknown>
  rejectionReasons: string[]
  isRejected: boolean
  smartScore?: number
  smartScoreBreakdown?: Record<string, unknown>
}

type QualityScoresMap =
  | Map<string, QualityScore>
  | { value?: Map<string, QualityScore>; set?: (k: string, v: QualityScore) => void }
  | Map<string, QualityScore>

describe('ManualSearchModal.vue', () => {
  const stubs = {
    PhMagnifyingGlass: true,
    PhX: true,
    PhSpinner: true,
    PhArrowClockwise: true,
    PhArrowUp: true,
    PhArrowDown: true,
    PhXCircle: true,
    PhDownloadSimple: true,
    PhArrowsDownUp: true,
    // Ensure ScorePopover renders its default slot in tests so the inner badge is present
    ScorePopover: { template: '<div><slot /></div>' },
    ...modalStubs,
  }

  // Helper to set `results` on the component instance in a way that works
  // whether the component exposes a ref (`.value`) or an unwrapped array.
  const setResultsOnVm = (vm: any, r: unknown) => {
    if (vm && vm.results && typeof vm.results === 'object' && 'value' in vm.results) {
      vm.results.value = r
    } else if (vm) {
      vm.results = r
    }
  }

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('uses details page for Usenet title links instead of direct NZB', async () => {
    const wrapper = mount(ManualSearchModal, {
      props: { isOpen: true, audiobook: null },
      global: { stubs },
    })
    const vm = wrapper.vm as any as {
      results: ManualSearchResult[]
      qualityScores?: QualityScoresMap
    }

    setResultsOnVm(vm, [
      {
        id: 'https://indexer/info/123',
        title: 'Test Usenet',
        downloadType: 'Usenet',
        resultUrl: '',
        sourceLink: 'https://indexer/info/123',
        nzbUrl: 'https://indexer/download/123.nzb',
        source: 'altHUB',
        size: 123,
      },
    ])

    await nextTick()

    // Debug: show rendered HTML to investigate missing anchor

    const anchor = wrapper.find('a.title-text')
    expect(anchor.exists()).toBe(true)
    expect(anchor.attributes('href')).toBe('https://indexer/info/123')
  })

  it('does not show language badge when language is Unknown', async () => {
    const wrapper = mount(ManualSearchModal, {
      props: { isOpen: true, audiobook: null },
      global: { stubs },
    })
    const vm = wrapper.vm as any as {
      results: ManualSearchResult[]
      qualityScores?: QualityScoresMap
    }

    setResultsOnVm(vm, [
      {
        id: 'u2',
        title: 'Lang Test',
        language: 'Unknown',
        downloadType: 'Usenet',
        resultUrl: 'https://indexer/info/2',
        source: 'alt',
        size: 0,
      },
    ])

    await nextTick()

    const langBadge = wrapper.find('.language-badge')
    expect(langBadge.exists()).toBe(false)
  })

  it('does not show duplicate format fallback when format equals quality', async () => {
    const wrapper = mount(ManualSearchModal, {
      props: { isOpen: true, audiobook: null },
      global: { stubs },
    })
    const vm = wrapper.vm as any as {
      results: ManualSearchResult[]
      qualityScores?: QualityScoresMap
    }

    setResultsOnVm(vm, [
      {
        id: 'q1',
        title: 'Format Fallback Test',
        quality: 'FLAC',
        format: 'FLAC',
        downloadType: 'Torrent',
        resultUrl: 'https://indexer/info/4',
        source: 'test',
        size: 0,
      },
    ])

    await nextTick()

    const badge = wrapper.find('.col-quality .quality-badge')
    expect(badge.exists()).toBe(true)
    expect(badge.text()).toContain('FLAC')
    // Should not contain duplicate 'FLAC' after the dot
    expect(badge.text()).not.toContain('FLAC · FLAC')
  })

  it('shows rejection reason instead of score for rejected results', async () => {
    const wrapper = mount(ManualSearchModal, {
      props: { isOpen: true, audiobook: null },
      global: { stubs },
    })
    const vm = wrapper.vm as any as {
      results: ManualSearchResult[]
      qualityScores?: QualityScoresMap
    }

    const fake = {
      id: 'r3',
      title: 'Rejected Test',
      downloadType: 'Torrent',
      resultUrl: 'https://indexer/info/3',
      source: 'test',
      size: 0,
    }

    setResultsOnVm(vm, [fake])

    const scoreObj: QualityScore = {
      searchResult: fake,
      totalScore: -1,
      scoreBreakdown: {},
      rejectionReasons: ['No seeds'],
      isRejected: true,
    }

    // Try to set via .value (ref) when available
    if (
      vm.qualityScores &&
      (
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value &&
      typeof (
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value!.set === 'function'
    ) {
      ;(
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value!.set('r3', scoreObj)
    }

    // Also set directly on the unwrapped proxy for compatibility with test runner behavior
    if (
      vm.qualityScores &&
      typeof (
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).set === 'function'
    ) {
      ;(
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).set!('r3', scoreObj)
    }

    await nextTick()

    const badge = wrapper.find('.col-score .score-badge.rejected')
    expect(badge.exists()).toBe(true)
    // Badge should read 'Rejected'
    expect(badge.text()).toContain('Rejected')
    // The title/hover should contain the rejection reason
    expect(badge.attributes('title')).toContain('No seeds')
  })

  it('shows Smart total as the score badge when smartScore is present', async () => {
    const wrapper = mount(ManualSearchModal, {
      props: { isOpen: true, audiobook: null },
      global: { stubs },
    })
    const vm = wrapper.vm as any as {
      results: ManualSearchResult[]
      qualityScores?: QualityScoresMap
    }

    setResultsOnVm(vm, [
      {
        id: 'r1',
        title: 'Smart Score Test',
        downloadType: 'Torrent',
        resultUrl: 'https://indexer/info/1',
        source: 'test',
        size: 0,
      },
    ])

    // Provide a quality score with a smartScore. Ensure both ref.value and unwrapped Map get the entry
    const scoreObj: QualityScore = {
      searchResult: vm.results[0],
      totalScore: 47,
      scoreBreakdown: { Quality: 65 },
      rejectionReasons: [],
      isRejected: false,
      smartScore: 12345,
      smartScoreBreakdown: { Quality: 65000 },
    }

    // Try to set via .value (ref) when available
    if (
      vm.qualityScores &&
      (
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value &&
      typeof (
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value!.set === 'function'
    ) {
      ;(
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value!.set('r1', scoreObj)
    }

    // Also set directly on the unwrapped proxy for compatibility with test runner behavior
    if (
      vm.qualityScores &&
      typeof (
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).set === 'function'
    ) {
      ;(
        vm.qualityScores as any as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).set!('r1', scoreObj)
    }

    // As a last-resort replace the Map entirely
    // Provide smartScoreBreakdown so the visible total is computed from component averages
    scoreObj.smartScore = 1234.5
    scoreObj.smartScoreBreakdown = { Quality: 90000, Format: 8500, Seed: 2000 }
    vm.qualityScores = new Map([['r1', scoreObj]])

    await nextTick()

    const badge = wrapper.find('.col-score .score-badge')
    expect(badge.exists()).toBe(true)
    // Normalized components: Quality=90, Format=85, Seed=20 -> avg ~65
    expect(badge.text()).toContain('65')
  })

  it('loads and attaches score data after search completes', async () => {
    vi.spyOn(apiService, 'getEnabledIndexers').mockResolvedValue([
      { id: 1, name: 'Test', implementation: 'Test', additionalSettings: null } as never,
    ])
    vi.spyOn(apiService, 'searchByApi').mockResolvedValue([
      {
        guid: 'score-result-1',
        title: 'Scored Result',
        size: 1024,
        publishDate: new Date().toISOString(),
        indexer: 'TestIndexer',
        indexerId: 1,
      } as never,
    ])
    vi.spyOn(apiService, 'getDefaultQualityProfile').mockResolvedValue({ id: 77 } as never)
    vi.spyOn(apiService, 'scoreSearchResults').mockImplementation(
      async (_profileId, searchResults) =>
        [
          {
            searchResult: searchResults[0],
            totalScore: 88,
            scoreBreakdown: { Quality: 88 },
            rejectionReasons: [],
            isRejected: false,
          },
        ] as never,
    )

    const wrapper = mount(ManualSearchModal, {
      props: {
        isOpen: false,
        audiobook: { id: 99, title: 'Target Book', authors: ['Author Name'] },
      },
      global: { stubs },
    })

    await wrapper.setProps({ isOpen: true })

    const start = Date.now()
    while (Date.now() - start < 3000) {
      await nextTick()
      const badge = wrapper.find('.col-score .score-badge')
      if (badge.exists() && badge.text().includes('88')) {
        break
      }
      await delay(25)
    }

    expect(apiService.getDefaultQualityProfile).toHaveBeenCalledTimes(1)
    expect(apiService.scoreSearchResults).toHaveBeenCalledTimes(1)

    const badge = wrapper.find('.col-score .score-badge')
    expect(badge.exists()).toBe(true)
    expect(badge.text()).toContain('88')
  })
})
