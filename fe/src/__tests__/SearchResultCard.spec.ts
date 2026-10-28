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
import { mount } from '@vue/test-utils'
import SearchResultCard from '@/components/search/SearchResultCard.vue'
import type { OpenLibraryBook, SearchResult } from '@/types'

describe('SearchResultCard', () => {
  const mockBook: OpenLibraryBook & {
    searchResult?: SearchResult
    imageUrl?: string
    metadataSource?: string
  } = {
    title: 'Dune',
    author_name: ['Frank Herbert'],
    first_publish_year: 1965,
    key: 'OL123M',
    imageUrl: 'https://example.com/dune.jpg',
    metadataSource: 'audible',
    searchResult: {
      title: 'Dune',
      authors: [{ name: 'Frank Herbert' }],
      narrator: 'Scott Brick',
      runtime: 900,
      language: 'english',
      subtitle: 'A Novel',
      publishedDate: '1965-06-01',
      id: 'B123',
      asin: 'B000123456',
      source: 'audible',
      artist: 'Frank Herbert',
      album: 'Dune',
      category: 'audiobook',
      format: 'm4b',
      size: 0,
      magnetLink: '',
      torrentUrl: '',
      nzbUrl: '',
      downloadType: 'Torrent',
    },
    publisher: ['Chilton'],
    seriesList: ['Dune #1'],
  }

  describe('rendering', () => {
    it('renders the card with all sections', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: mockBook,
          coverUrl: 'https://example.com/dune.jpg',
        },
      })

      expect(wrapper.find('.title-result-card').exists()).toBe(true)
      expect(wrapper.find('.result-poster').exists()).toBe(true)
      expect(wrapper.find('.result-info').exists()).toBe(true)
      expect(wrapper.find('.result-actions').exists()).toBe(true)
    })

    it('displays book title', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('Dune')
    })

    it('displays placeholder image when no cover URL', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: mockBook,
          coverUrl: undefined,
        },
      })

      const img = wrapper.find('.placeholder-cover-image')
      expect(img.exists()).toBe(true)
      expect(img.attributes('alt')).toBe('Cover unavailable')
    })

    it('displays actual cover image when URL provided', () => {
      const coverUrl = 'https://example.com/dune.jpg'
      const wrapper = mount(SearchResultCard, {
        props: {
          book: mockBook,
          coverUrl,
        },
      })

      const img = wrapper.findAll('img')[0]
      expect(img.attributes('src')).toBe(coverUrl)
    })
  })

  describe('book information', () => {
    it('displays author names from author_name array', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            author_name: ['Frank Herbert', 'James O.A. Dune'],
          },
        },
      })

      expect(wrapper.text()).toContain('by Frank Herbert, James O.A. Dune')
    })

    it('displays narrator information', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('Narrated by Scott Brick')
    })

    it('displays subtitle when available', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('A Novel')
    })

    it('displays publisher', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('Chilton')
    })

    it('displays runtime', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('15h')
    })

    it('displays language', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('English')
    })

    it('displays series information', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('Dune #1')
    })

    it('displays publish year', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('1965')
    })

    it('displays ASIN when available', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('B000123456')
    })
  })

  describe('metadata badges', () => {
    it('shows ASIN badge when available', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            searchResult: { ...mockBook.searchResult, asin: 'B000123456' },
          },
        },
      })

      const badges = wrapper.findAll('.metadata-badge')
      const asinBadge = badges.find((b) => b.text().includes('B000123456'))
      expect(asinBadge).toBeDefined()
    })

    it('shows OpenLibrary ID when no ASIN', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            searchResult: { ...mockBook.searchResult, asin: undefined, id: 'OL123M' },
          },
        },
      })

      const badges = wrapper.findAll('.metadata-badge')
      const idBadge = badges.find((b) => b.text().includes('OL123M'))
      expect(idBadge).toBeDefined()
    })

    it('shows date badge with formatted date', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      expect(wrapper.text()).toContain('Jun 01, 1965')
    })
  })

  describe('action buttons', () => {
    it('renders add button', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      const buttons = wrapper.findAll('.btn')
      expect(buttons.length).toBeGreaterThanOrEqual(1)
      expect(buttons[0].text()).toContain('Add to Library')
    })

    it('shows Add button as primary when not added', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: mockBook,
          isAdded: false,
        },
      })

      const addBtn = wrapper.findAll('.btn')[0]
      expect(addBtn.classes()).toContain('btn-primary')
      expect(addBtn.text()).toContain('Add to Library')
    })

    it('shows Added button as success when already added', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: mockBook,
          isAdded: true,
        },
      })

      const addBtn = wrapper.findAll('.btn')[0]
      expect(addBtn.classes()).toContain('btn-success')
      expect(addBtn.text()).toContain('Added')
      expect(addBtn.attributes('disabled')).toBeDefined()
    })

    it('emits add event when add button clicked', async () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
      })

      await wrapper.findAll('.btn')[0].trigger('click')
      expect(wrapper.emitted('add')).toBeTruthy()
    })

    it('disables add button when isAdded is true', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: mockBook,
          isAdded: true,
        },
      })

      const addBtn = wrapper.findAll('.btn')[0]
      expect(addBtn.attributes('disabled')).toBe('')
    })
  })

  describe('slots', () => {
    it('allows overriding title slot', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
        slots: {
          title: '<div class="custom-title">Custom Title</div>',
        },
      })

      expect(wrapper.find('.custom-title').exists()).toBe(true)
      expect(wrapper.text()).toContain('Custom Title')
    })

    it('allows overriding author slot', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
        slots: {
          author: '<div class="custom-author">Custom Author</div>',
        },
      })

      expect(wrapper.find('.custom-author').exists()).toBe(true)
    })

    it('allows overriding actions slot', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
        slots: {
          actions: '<button class="custom-action">Custom Action</button>',
        },
      })

      expect(wrapper.find('.custom-action').exists()).toBe(true)
      expect(wrapper.find('.custom-action').text()).toBe('Custom Action')
    })

    it('allows overriding stats slot', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
        slots: {
          stats: '<div class="custom-stats">Custom Stats</div>',
        },
      })

      expect(wrapper.find('.custom-stats').exists()).toBe(true)
    })

    it('allows overriding metadata slot', () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook },
        slots: {
          metadata: '<div class="custom-metadata">Custom Metadata</div>',
        },
      })

      expect(wrapper.find('.custom-metadata').exists()).toBe(true)
    })
  })

  describe('edge cases', () => {
    it('handles missing searchResult gracefully', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            title: 'Test Book',
            author_name: ['Author'],
            key: 'OL123M',
          } as unknown,
        },
      })

      expect(wrapper.find('.title-result-card').exists()).toBe(true)
    })

    it('handles missing narrator gracefully', () => {
      const bookWithoutNarrator = {
        ...mockBook,
        searchResult: { ...mockBook.searchResult, narrator: undefined },
      }

      const wrapper = mount(SearchResultCard, {
        props: { book: bookWithoutNarrator },
      })

      const narratorText = wrapper.text()
      expect(narratorText).not.toContain('Narrated by undefined')
    })

    it('handles empty author_name array', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            author_name: [],
          },
        },
      })

      expect(wrapper.text()).toContain('by Unknown Author')
    })

    it('handles missing runtime', () => {
      const bookWithoutRuntime = {
        ...mockBook,
        searchResult: { ...mockBook.searchResult, runtime: undefined },
      }

      const wrapper = mount(SearchResultCard, {
        props: { book: bookWithoutRuntime },
      })

      const text = wrapper.text()
      expect(text).not.toContain('NaN')
    })

    it('handles books without series', () => {
      const bookWithoutSeries = { ...mockBook, seriesList: undefined }

      const wrapper = mount(SearchResultCard, {
        props: { book: bookWithoutSeries },
      })

      expect(wrapper.find('.result-series').exists()).toBe(false)
    })
  })

  describe('image error handling', () => {
    it('emits image-error event when image fails to load', async () => {
      const wrapper = mount(SearchResultCard, {
        props: { book: mockBook, coverUrl: 'https://example.com/invalid.jpg' },
      })

      const img = wrapper.find('img')
      await img.trigger('error')

      expect(wrapper.emitted('image-error')).toBeTruthy()
    })
  })

  describe('ID priority', () => {
    it('prefers ASIN as primary ID', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            asin: 'B000123456',
            key: 'OL123M',
          },
        },
      })

      const text = wrapper.text()
      expect(text).toContain('B000123456')
      expect(text).not.toContain('OL123M')
    })

    it('uses searchResult.asin when book.asin not available', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            asin: undefined,
            searchResult: {
              ...mockBook.searchResult,
              asin: 'B000123456',
            },
          },
        },
      })

      const text = wrapper.text()
      expect(text).toContain('B000123456')
    })

    it('falls back to key for ID when no ASIN', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            asin: undefined,
            key: 'CUSTOM_ID_123',
            searchResult: {
              ...mockBook.searchResult,
              asin: undefined,
            },
          },
        },
      })

      const text = wrapper.text()
      expect(text).toContain('CUSTOM_ID_123')
    })
  })

  describe('metadata source display', () => {
    it('shows Audible label for audible-backed metadata sources', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            metadataSource: 'audible',
          },
        },
      })

      const text = wrapper.text()
      expect(text).toContain('Audible')
    })

    it('shows custom metadata source for other sources', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            metadataSource: 'openlibrary',
          },
        },
      })

      const text = wrapper.text()
      expect(text).toContain('Metadata: openlibrary')
    })

    it('shows nested metadata source badges', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            metadataSource: undefined,
            searchResult: {
              ...mockBook.searchResult,
              metadataSource: undefined,
              searchResult: {
                metadataSource: 'Amazon',
              },
            } as unknown as SearchResult,
          },
        },
      })

      const badge = wrapper.find('.metadata-source-badge')
      expect(badge.exists()).toBe(true)
      expect(badge.attributes('data-source')).toBe('Amazon')
      expect(badge.text()).toContain('Metadata: Amazon')
    })

    it('labels metadata links from nested metadata source', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            metadataSource: undefined,
            searchResult: {
              ...mockBook.searchResult,
              metadataSource: undefined,
              searchResult: {
                metadataSource: 'Amazon',
              },
            } as unknown as SearchResult,
          },
          metadataSourceUrl: 'https://www.amazon.de/dp/BAMZ2',
        },
      })

      const link = wrapper.find('.metadata-source-link')
      expect(link.exists()).toBe(true)
      expect(link.attributes('href')).toBe('https://www.amazon.de/dp/BAMZ2')
      expect(link.attributes('data-source')).toBe('Amazon')
      expect(link.text()).toContain('Metadata: Amazon')
    })

    it('combines metadata and source links when urls match', () => {
      const wrapper = mount(SearchResultCard, {
        props: {
          book: {
            ...mockBook,
            metadataSource: 'audible',
          },
          metadataSourceUrl: 'https://www.audible.de/pd/B01M02FJ7A',
          sourceUrl: 'https://www.audible.de/pd/B01M02FJ7A',
        },
      })

      const links = wrapper.findAll('.result-meta a')
      expect(links).toHaveLength(1)
      const link = links[0]!
      expect(link.classes()).toContain('metadata-source-link')
      expect(link.classes()).toContain('source-link')
      expect(link.attributes('href')).toBe('https://www.audible.de/pd/B01M02FJ7A')
      expect(link.text()).toContain('Audible')
      expect(link.findAll('svg')).toHaveLength(2)
    })
  })
})
