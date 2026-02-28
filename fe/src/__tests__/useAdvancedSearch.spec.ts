import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { useAdvancedSearch } from '@/composables/useAdvancedSearch'

describe('useAdvancedSearch', () => {
  let localStorageMock: Record<string, string> = {}

  beforeEach(() => {
    // Mock localStorage
    localStorageMock = {}
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => localStorageMock[key] || null,
      setItem: (key: string, value: string) => {
        localStorageMock[key] = value
      },
      removeItem: (key: string) => {
        delete localStorageMock[key]
      },
      clear: () => {
        localStorageMock = {}
      },
    })
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.useRealTimers()
  })

  describe('initialization', () => {
    it('initializes with empty form state', () => {
      const { advancedSearchParams, showAdvancedSearch } = useAdvancedSearch()

      expect(showAdvancedSearch.value).toBe(false)
      expect(advancedSearchParams.value).toEqual({
        title: '',
        author: '',
        isbn: '',
        series: '',
        asin: '',
        language: '',
      })
    })

    it('restores persisted state from localStorage', () => {
      const savedState = {
        showAdvanced: true,
        params: {
          title: 'Dune',
          author: 'Frank Herbert',
          isbn: '',
          series: '',
          asin: 'B123',
          language: 'english',
        },
      }
      localStorageMock['listenarr.addnew.advanced'] = JSON.stringify(savedState)

      const { advancedSearchParams, showAdvancedSearch } = useAdvancedSearch()

      expect(showAdvancedSearch.value).toBe(true)
      expect(advancedSearchParams.value).toEqual(savedState.params)
    })

    it('handles corrupted localStorage data gracefully', () => {
      localStorageMock['listenarr.addnew.advanced'] = 'invalid json'

      const { showAdvancedSearch, advancedSearchParams } = useAdvancedSearch()

      expect(showAdvancedSearch.value).toBe(false)
      expect(advancedSearchParams.value.title).toBe('')
    })

    it('handles missing showAdvanced in stored state', () => {
      const savedState = {
        params: {
          title: 'Dune',
          author: '',
          isbn: '',
          series: '',
          asin: '',
          language: '',
        },
      }
      localStorageMock['listenarr.addnew.advanced'] = JSON.stringify(savedState)

      const { showAdvancedSearch, advancedSearchParams } = useAdvancedSearch()

      expect(showAdvancedSearch.value).toBe(false)
      expect(advancedSearchParams.value.title).toBe('Dune')
    })
  })

  describe('form validation', () => {
    it('returns false when all fields are empty', () => {
      const { isValidAdvancedSearch } = useAdvancedSearch()

      expect(isValidAdvancedSearch.value).toBe(false)
    })

    it('returns true when title is provided', () => {
      const { advancedSearchParams, isValidAdvancedSearch } = useAdvancedSearch()

      advancedSearchParams.value.title = 'Dune'

      expect(isValidAdvancedSearch.value).toBe(true)
    })

    it('returns true when author is provided', () => {
      const { advancedSearchParams, isValidAdvancedSearch } = useAdvancedSearch()

      advancedSearchParams.value.author = 'Frank Herbert'

      expect(isValidAdvancedSearch.value).toBe(true)
    })

    it('returns true when isbn is provided', () => {
      const { advancedSearchParams, isValidAdvancedSearch } = useAdvancedSearch()

      advancedSearchParams.value.isbn = '0-441-13959-0'

      expect(isValidAdvancedSearch.value).toBe(true)
    })

    it('returns true when asin is provided', () => {
      const { advancedSearchParams, isValidAdvancedSearch } = useAdvancedSearch()

      advancedSearchParams.value.asin = 'B000123456'

      expect(isValidAdvancedSearch.value).toBe(true)
    })

    it('returns true when series is provided', () => {
      const { advancedSearchParams, isValidAdvancedSearch } = useAdvancedSearch()

      advancedSearchParams.value.series = 'Expanse'

      expect(isValidAdvancedSearch.value).toBe(true)
    })

    it('ignores whitespace-only fields', () => {
      const { advancedSearchParams, isValidAdvancedSearch } = useAdvancedSearch()

      advancedSearchParams.value.title = '   '
      advancedSearchParams.value.author = '  \n  '

      expect(isValidAdvancedSearch.value).toBe(false)
    })

    it('returns true with multiple fields filled', () => {
      const { advancedSearchParams, isValidAdvancedSearch } = useAdvancedSearch()

      advancedSearchParams.value.title = 'Dune'
      advancedSearchParams.value.author = 'Frank Herbert'

      expect(isValidAdvancedSearch.value).toBe(true)
    })
  })

  describe('toggleAdvancedSearch', () => {
    it('toggles visibility on and off', () => {
      const { showAdvancedSearch, toggleAdvancedSearch } = useAdvancedSearch()

      expect(showAdvancedSearch.value).toBe(false)

      toggleAdvancedSearch()
      expect(showAdvancedSearch.value).toBe(true)

      toggleAdvancedSearch()
      expect(showAdvancedSearch.value).toBe(false)
    })

    it('persists visibility change', () => {
      const { toggleAdvancedSearch } = useAdvancedSearch()

      toggleAdvancedSearch()
      vi.advanceTimersByTime(300)

      const saved = localStorageMock['listenarr.addnew.advanced']
      expect(saved).toBeDefined()
      const parsed = JSON.parse(saved)
      expect(parsed.showAdvanced).toBe(true)
    })
  })

  describe('updateSearchParam', () => {
    it('updates individual search parameters', () => {
      const { advancedSearchParams, updateSearchParam } = useAdvancedSearch()

      updateSearchParam('title', 'Dune')
      expect(advancedSearchParams.value.title).toBe('Dune')

      updateSearchParam('author', 'Frank Herbert')
      expect(advancedSearchParams.value.author).toBe('Frank Herbert')

      updateSearchParam('asin', 'B123')
      expect(advancedSearchParams.value.asin).toBe('B123')
    })

    it('persists parameter changes', () => {
      const { updateSearchParam } = useAdvancedSearch()

      updateSearchParam('title', 'Dune')
      vi.advanceTimersByTime(300)

      const saved = localStorageMock['listenarr.addnew.advanced']
      expect(saved).toBeDefined()
      const parsed = JSON.parse(saved)
      expect(parsed.params.title).toBe('Dune')
    })

    it('debounces multiple updates', () => {
      const { updateSearchParam } = useAdvancedSearch()
      const setItemSpy = vi.spyOn(localStorage, 'setItem')

      updateSearchParam('title', 'Dune')
      vi.advanceTimersByTime(100)
      updateSearchParam('title', 'Dune Messiah')
      vi.advanceTimersByTime(100)

      // Should still be debouncing
      expect(setItemSpy).not.toHaveBeenCalled()

      vi.advanceTimersByTime(100)

      // Now should have saved
      expect(setItemSpy).toHaveBeenCalledTimes(1)
    })
  })

  describe('resetAdvancedSearch', () => {
    it('clears all form fields', () => {
      const { advancedSearchParams, resetAdvancedSearch } = useAdvancedSearch()

      advancedSearchParams.value.title = 'Dune'
      advancedSearchParams.value.author = 'Frank Herbert'
      advancedSearchParams.value.asin = 'B123'

      resetAdvancedSearch()

      expect(advancedSearchParams.value).toEqual({
        title: '',
        author: '',
        isbn: '',
        series: '',
        asin: '',
        language: '',
      })
    })

    it('persists reset to localStorage', () => {
      const { advancedSearchParams, resetAdvancedSearch } = useAdvancedSearch()

      advancedSearchParams.value.title = 'Dune'
      resetAdvancedSearch()
      vi.advanceTimersByTime(300)

      const saved = localStorageMock['listenarr.addnew.advanced']
      expect(saved).toBeDefined()
      const parsed = JSON.parse(saved)
      expect(parsed.params.title).toBe('')
    })
  })

  describe('getSearchQuery', () => {
    it('returns current search parameters', () => {
      const { getSearchQuery, updateSearchParam } = useAdvancedSearch()

      updateSearchParam('title', 'Dune')
      updateSearchParam('author', 'Frank Herbert')

      const query = getSearchQuery()

      expect(query).toEqual({
        title: 'Dune',
        author: 'Frank Herbert',
        isbn: '',
        series: '',
        asin: '',
        language: '',
      })
    })

    it('returns same object reference as params ref', () => {
      const { advancedSearchParams, getSearchQuery } = useAdvancedSearch()

      const query = getSearchQuery()

      expect(query).toBe(advancedSearchParams.value)
    })
  })

  describe('clearPersistedState', () => {
    it('removes state from localStorage', () => {
      const { updateSearchParam, clearPersistedState } = useAdvancedSearch()

      updateSearchParam('title', 'Dune')
      vi.advanceTimersByTime(300)

      expect(localStorageMock['listenarr.addnew.advanced']).toBeDefined()

      clearPersistedState()

      expect(localStorageMock['listenarr.addnew.advanced']).toBeUndefined()
    })

    it('clears pending save timer', () => {
      const { updateSearchParam, clearPersistedState } = useAdvancedSearch()

      updateSearchParam('title', 'Dune')
      // Advance partway through debounce
      vi.advanceTimersByTime(100)

      clearPersistedState()

      // Complete the debounce period
      vi.advanceTimersByTime(200)

      // Should not have saved (timer was cleared)
      expect(localStorageMock['listenarr.addnew.advanced']).toBeUndefined()
    })
  })

  describe('localStorage debouncing', () => {
    it('debounces rapid parameter changes', () => {
      const { updateSearchParam } = useAdvancedSearch()
      const setItemSpy = vi.spyOn(localStorage, 'setItem')

      // Simulate rapid user typing
      updateSearchParam('title', 'D')
      vi.advanceTimersByTime(100)
      updateSearchParam('title', 'Du')
      vi.advanceTimersByTime(100)
      updateSearchParam('title', 'Dun')
      vi.advanceTimersByTime(100)
      updateSearchParam('title', 'Dune')

      // No saves yet
      expect(setItemSpy).not.toHaveBeenCalled()

      // Wait for debounce to complete
      vi.advanceTimersByTime(300)

      // Should save only once, with final value
      expect(setItemSpy).toHaveBeenCalledTimes(1)
      const saved = JSON.parse(localStorageMock['listenarr.addnew.advanced'])
      expect(saved.params.title).toBe('Dune')
    })

    it('saves immediately after debounce window expires', () => {
      const { updateSearchParam } = useAdvancedSearch()
      const setItemSpy = vi.spyOn(localStorage, 'setItem')

      updateSearchParam('title', 'Dune')
      vi.advanceTimersByTime(300)

      expect(setItemSpy).toHaveBeenCalledTimes(1)

      updateSearchParam('author', 'Frank Herbert')
      vi.advanceTimersByTime(300)

      expect(setItemSpy).toHaveBeenCalledTimes(2)
    })
  })

  describe('cleanup', () => {
    it('clears pending save timer on cleanup', () => {
      const { updateSearchParam, cleanup } = useAdvancedSearch()

      updateSearchParam('title', 'Dune')
      vi.advanceTimersByTime(100)

      cleanup()

      // Complete the debounce period
      vi.advanceTimersByTime(300)

      // Should not have saved because timer was cleared
      expect(localStorageMock['listenarr.addnew.advanced']).toBeUndefined()
    })
  })

  describe('reactivity', () => {
    it('updates validation when parameters change', () => {
      const { advancedSearchParams, isValidAdvancedSearch } = useAdvancedSearch()

      expect(isValidAdvancedSearch.value).toBe(false)

      advancedSearchParams.value.title = 'Dune'

      expect(isValidAdvancedSearch.value).toBe(true)

      advancedSearchParams.value.title = ''

      expect(isValidAdvancedSearch.value).toBe(false)
    })

    it('tracks nested parameter updates', () => {
      const { advancedSearchParams } = useAdvancedSearch()
      const setItemSpy = vi.spyOn(localStorage, 'setItem')

      advancedSearchParams.value.author = 'Frank Herbert'
      vi.advanceTimersByTime(300)

      expect(setItemSpy).toHaveBeenCalledTimes(1)
    })
  })

  describe('error handling', () => {
    it('handles localStorage quota exceeded gracefully', () => {
      const { updateSearchParam } = useAdvancedSearch()

      // Mock localStorage to throw quota error
      vi.spyOn(localStorage, 'setItem').mockImplementationOnce(() => {
        throw new Error('QuotaExceededError')
      })

      // Should not throw
      expect(() => {
        updateSearchParam('title', 'Dune')
        vi.advanceTimersByTime(300)
      }).not.toThrow()
    })

    it('handles null stored state gracefully', () => {
      localStorageMock['listenarr.addnew.advanced'] = 'null'

      const { showAdvancedSearch } = useAdvancedSearch()

      expect(showAdvancedSearch.value).toBe(false)
    })

    it('handles removal of non-existent key gracefully', () => {
      const { clearPersistedState } = useAdvancedSearch()

      expect(() => {
        clearPersistedState()
        clearPersistedState() // Call twice
      }).not.toThrow()
    })
  })
})
