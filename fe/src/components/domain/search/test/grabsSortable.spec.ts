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
import { createIndexer } from '@/test/factories/indexer'
import { createQualityProfile } from '@/test/factories/qualityProfile'
import { createSearchResult } from '@/test/factories/searchResult'
import { delay, waitFor } from '@/test/utils/wait'
const { apiService } = apiModule

// Ensure instance method exists for legacy spies used in tests
if (!(apiService as any).getEnabledIndexers) {
  ;(apiService as any).getEnabledIndexers = async () => []
}
if (!(apiService as any).searchByApi) {
  ;(apiService as any).searchByApi = async () => []
}
if (!(apiService as any).getDefaultQualityProfile) {
  ;(apiService as any).getDefaultQualityProfile = async () => ({ id: 1 })
}
if (!(apiService as any).scoreSearchResults) {
  ;(apiService as any).scoreSearchResults = async () => []
}

describe('ManualSearchModal - grabs sorting', () => {
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
    ScorePopover: { template: '<div><slot /></div>' },
    ...modalStubs,
  }

  afterEach(() => {
    vi.restoreAllMocks()
  })

  const triggerSearchAndWait = async (wrapper, selector: string, timeout = 3000) => {
    try {
      await (wrapper.vm as any as { search?: () => Promise<void> }).search?.()
    } catch {}
    await nextTick()
    await waitFor(() => wrapper.find(selector).exists(), { timeout })
  }

  it('header is clickable to set Grabs sort', async () => {
    // Mock instance methods on apiService so component calls succeed
    vi.spyOn(apiService, 'getEnabledIndexers').mockResolvedValue([createIndexer()])
    vi.spyOn(apiService, 'searchByApi').mockResolvedValue([
      createSearchResult({
        guid: '1',
        title: 'A',
        grabs: 100,
        publishDate: new Date().toISOString(),
        indexer: 'Test',
        indexerId: 1,
      }),
      createSearchResult({
        guid: '2',
        title: 'B',
        grabs: 10,
        publishDate: new Date().toISOString(),
        indexer: 'Test',
        indexerId: 1,
      }),
      createSearchResult({
        guid: '3',
        title: 'C',
        grabs: 50,
        publishDate: new Date().toISOString(),
        indexer: 'Test',
        indexerId: 1,
      }),
    ])
    vi.spyOn(apiService, 'getDefaultQualityProfile').mockResolvedValue(createQualityProfile())
    vi.spyOn(apiService, 'scoreSearchResults').mockResolvedValue([])

    const wrapper = mount(ManualSearchModal, {
      props: { isOpen: false, audiobook: { id: 1, title: 'Test', authors: [] } },
      global: { stubs },
    })
    await wrapper.setProps({ isOpen: true })

    // Force the component to run search() in test env and wait for table header
    await (wrapper.vm as any as { search?: () => Promise<void> }).search?.()
    await triggerSearchAndWait(wrapper, 'th.col-grabs')
    await nextTick()

    const header = wrapper.find('th.col-grabs')
    expect(header.exists()).toBe(true)

    // First click: set to Grabs (new column) -> defaults to Descending
    await header.trigger('click')
    await delay(100)
    await nextTick()

    // Read grabs values from rows in order
    const rowsAfterDesc = wrapper.findAll('tbody tr')
    const grabsDesc = rowsAfterDesc.map((r) => {
      const badge = r.find('td.col-grabs .grabs-badge')
      const txt = badge.exists() ? badge.text() : ''
      return Number((txt || '').replace(/[^0-9]/g, '').trim())
    })
    expect(grabsDesc).toEqual([100, 50, 10])

    // Second click: same column -> toggles to Ascending
    await header.trigger('click')
    await delay(100)
    await nextTick()

    const rowsAfterAsc = wrapper.findAll('tbody tr')
    const grabsAsc = rowsAfterAsc.map((r) => {
      const badge = r.find('td.col-grabs .grabs-badge')
      const txt = badge.exists() ? badge.text() : ''
      return Number((txt || '').replace(/[^0-9]/g, '').trim())
    })
    expect(grabsAsc).toEqual([10, 50, 100])
  })

  it('header is clickable to set Language sort and toggles order', async () => {
    // Mock instance methods on apiService so component calls succeed
    vi.spyOn(apiService, 'getEnabledIndexers').mockResolvedValue([createIndexer()])
    vi.spyOn(apiService, 'searchByApi').mockResolvedValue([
      createSearchResult({
        guid: '1',
        title: 'A',
        grabs: 0,
        publishDate: new Date().toISOString(),
        language: 'Spanish',
        indexer: 'Test',
        indexerId: 1,
      }),
      createSearchResult({
        guid: '2',
        title: 'B',
        grabs: 0,
        publishDate: new Date().toISOString(),
        language: 'English',
        indexer: 'Test',
        indexerId: 1,
      }),
      createSearchResult({
        guid: '3',
        title: 'C',
        grabs: 0,
        publishDate: new Date().toISOString(),
        language: 'French',
        indexer: 'Test',
        indexerId: 1,
      }),
    ])
    vi.spyOn(apiService, 'getDefaultQualityProfile').mockResolvedValue(createQualityProfile())
    vi.spyOn(apiService, 'scoreSearchResults').mockResolvedValue([])

    const wrapper = mount(ManualSearchModal, {
      props: { isOpen: false, audiobook: { id: 1, title: 'Test', authors: [] } },
      global: { stubs },
    })
    await wrapper.setProps({ isOpen: true })

    // Force the component to run search() in test env and wait for table header
    await (wrapper.vm as any as { search?: () => Promise<void> }).search?.()
    await triggerSearchAndWait(wrapper, 'th.col-language')
    await nextTick()

    const header = wrapper.find('th.col-language')
    expect(header.exists()).toBe(true)

    // First click: set Language -> defaults to Descending (Z->A)
    await header.trigger('click')
    await delay(100)
    await nextTick()

    const rowsDesc = wrapper.findAll('tbody tr')
    const langsDesc = rowsDesc.map((r) => r.find('td.col-language .language-badge').text())
    // Descending alphabetical: Spanish, French, English
    expect(langsDesc).toEqual(['Spanish', 'French', 'English'])

    // Second click toggles to Ascending
    await header.trigger('click')
    await delay(100)
    await nextTick()

    const rowsAsc = wrapper.findAll('tbody tr')
    const langsAsc = rowsAsc.map((r) => r.find('td.col-language .language-badge').text())
    expect(langsAsc).toEqual(['English', 'French', 'Spanish'])
  })
})
