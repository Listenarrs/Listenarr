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
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import FindMatchResults from '@/components/domain/audiobook/FindMatchResults.vue'
import { useConfigurationStore } from '@/stores/configuration'
import type { ApplicationSettings, SearchResult } from '@/types'

const record = (asin: string, language?: string) =>
  ({ asin, title: `Primal Hunter ${asin}`, language }) as unknown as SearchResult

function mountResults(results: SearchResult[]) {
  return mount(FindMatchResults, { props: { results } })
}

function preferLanguage(defaultSearchLanguage: string) {
  useConfigurationStore().applicationSettings = {
    defaultSearchLanguage,
  } as ApplicationSettings
}

const tags = (wrapper: ReturnType<typeof mountResults>) =>
  wrapper.findAll('[data-testid="find-match-language"]').map((tag) => ({
    text: tag.text(),
    preferred: tag.classes().includes('preferred'),
  }))

describe('FindMatchResults', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('tags each record with its language: green for the preferred one, grey for the others', () => {
    preferLanguage('french')
    const wrapper = mountResults([
      record('B0FR000001', 'french'),
      record('B0DE000001', 'german'),
      record('B0US000001', 'English'),
    ])

    expect(tags(wrapper)).toEqual([
      { text: 'French', preferred: true },
      { text: 'German', preferred: false },
      { text: 'English', preferred: false },
    ])
  })

  it('tags a record Audible gives no language for in grey', () => {
    preferLanguage('english')
    const wrapper = mountResults([record('B0XX000001')])

    expect(tags(wrapper)).toEqual([{ text: 'Language unknown', preferred: false }])
  })

  it('keeps a language it does not know, in grey', () => {
    preferLanguage('english')
    const wrapper = mountResults([record('B0XX000002', 'klingon')])

    expect(tags(wrapper)).toEqual([{ text: 'Klingon', preferred: false }])
  })

  it('prefers no language when the search language is All', () => {
    preferLanguage('all')
    const wrapper = mountResults([record('B0US000002', 'english')])

    expect(tags(wrapper)).toEqual([{ text: 'English', preferred: false }])
  })
})
