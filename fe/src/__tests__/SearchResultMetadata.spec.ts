import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import SearchResultMetadata from '@/components/search/SearchResultMetadata.vue'

describe('SearchResultMetadata', () => {
  it('renders nothing when no metadata provided', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {},
    })
    expect(wrapper.find('.metadata-badges').exists()).toBe(false)
  })

  it('renders publisher badge', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        publisher: 'Penguin Books',
      },
    })
    expect(wrapper.text()).toContain('Penguin Books')
  })

  it('renders published date when available', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        publishedDate: '2015-10-05',
      },
    })
    expect(wrapper.text()).toContain('Oct 05, 2015')
  })

  it('falls back to publishYear when publishedDate not available', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        publishYear: 2015,
      },
    })
    expect(wrapper.text()).toContain('2015')
  })

  it('prefers publishedDate over publishYear', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        publishedDate: '2020-05-15',
        publishYear: 2015,
      },
    })
    expect(wrapper.text()).toContain('May 15, 2020')
    expect(wrapper.text()).not.toContain('2015')
  })

  it('renders ASIN badge', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        asin: 'B000123456',
      },
    })
    expect(wrapper.text()).toContain('B000123456')
  })

  it('renders ISBN badge', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        isbn: '978-0-141-04724-1',
      },
    })
    expect(wrapper.text()).toContain('978-0-141-04724-1')
  })

  it('renders OpenLibrary ID when no ASIN', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        openLibraryId: '/works/OL45883W',
      },
    })
    expect(wrapper.text()).toContain('/works/OL45883W')
  })

  it('skips OpenLibrary ID when ASIN is present', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        asin: 'B000123456',
        openLibraryId: '/works/OL45883W',
      },
    })
    expect(wrapper.text()).toContain('B000123456')
    expect(wrapper.text()).not.toContain('/works/OL45883W')
  })

  it('renders multiple badges', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        publisher: 'Penguin',
        publishedDate: '2015-10-05',
        asin: 'B000123456',
      },
    })
    const badges = wrapper.findAll('.metadata-badge')
    expect(badges.length).toBe(3)
    expect(wrapper.text()).toContain('Penguin')
    expect(wrapper.text()).toContain('Oct 05, 2015')
    expect(wrapper.text()).toContain('B000123456')
  })

  it('handles special characters in publisher name', () => {
    const wrapper = mount(SearchResultMetadata, {
      props: {
        publisher: "O'Reilly & Associates",
      },
    })
    expect(wrapper.text()).toContain("O'Reilly & Associates")
  })
})
