import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { describe, it, expect } from 'vitest'
import ManualSearchModal from '@/components/domain/search/ManualSearchModal.vue'

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
    Modal: { template: '<div><slot name="header" /><slot /></div>' },
    ModalHeader: { template: '<div><slot /></div>' },
    ModalBody: { template: '<div><slot /></div>' },
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

  it('uses details page for Usenet title links instead of direct NZB', async () => {
    const wrapper = mount(ManualSearchModal, {
      props: { isOpen: true, audiobook: null },
      global: { stubs },
    })
    const vm = wrapper.vm as unknown as {
      results: ManualSearchResult[]
      qualityScores?: QualityScoresMap
    }

    // Set a usenet-style result where id is an informational URL that should be used for the title link
    // Support both raw arrays and refs (test runner may expose refs differently)
    const setResults = (r: unknown) => {
      if (vm && (vm as any).results && typeof (vm as any).results === 'object' && 'value' in (vm as any).results) {
        ;(vm as any).results.value = r
      } else if (vm) {
        ;(vm as any).results = r
      }
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
    // eslint-disable-next-line no-console
    console.log(wrapper.html())
    const anchor = wrapper.find('a.title-text')
    expect(anchor.exists()).toBe(true)
    expect(anchor.attributes('href')).toBe('https://indexer/info/123')
  })

  it('does not show language badge when language is Unknown', async () => {
    const wrapper = mount(ManualSearchModal, {
      props: { isOpen: true, audiobook: null },
      global: { stubs },
    })
    const vm = wrapper.vm as unknown as {
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
    const vm = wrapper.vm as unknown as {
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
    const vm = wrapper.vm as unknown as {
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
        vm.qualityScores as unknown as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value &&
      typeof (
        vm.qualityScores as unknown as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value!.set === 'function'
    ) {
      ;(
        vm.qualityScores as unknown as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value!.set('r3', scoreObj)
    }

    // Also set directly on the unwrapped proxy for compatibility with test runner behavior
    if (
      vm.qualityScores &&
      typeof (
        vm.qualityScores as unknown as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).set === 'function'
    ) {
      ;(
        vm.qualityScores as unknown as {
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
    const vm = wrapper.vm as unknown as {
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
        vm.qualityScores as unknown as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value &&
      typeof (
        vm.qualityScores as unknown as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value!.set === 'function'
    ) {
      ;(
        vm.qualityScores as unknown as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).value!.set('r1', scoreObj)
    }

    // Also set directly on the unwrapped proxy for compatibility with test runner behavior
    if (
      vm.qualityScores &&
      typeof (
        vm.qualityScores as unknown as {
          value?: Map<string, QualityScore>
          set?: (k: string, v: QualityScore) => void
        }
      ).set === 'function'
    ) {
      ;(
        vm.qualityScores as unknown as {
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
})
