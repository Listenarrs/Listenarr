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
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { nextTick } from 'vue'

const getLibraryIssues = vi.hoisted(() => vi.fn())
vi.mock('@/services/api', () => ({ apiService: { getLibraryIssues } }))

import { useLibraryIssuesStore } from '@/stores/libraryIssues'

describe('libraryIssues store', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    window.localStorage.clear()
    setActivePinia(createPinia())
    getLibraryIssues.mockResolvedValue([
      { id: 7, title: 'Primal Hunter 4', issues: ['missing_region'] },
      { id: 9, title: 'dfh', issues: ['invalid_asin'], ignored: true },
      { id: 11, title: 'Ready Player One', issues: ['missing_asin'], ignored: true },
    ])
  })

  it('never counts ignored books in the menu, whether they are listed or not', async () => {
    const store = useLibraryIssuesStore()
    await store.load()
    expect(store.showIgnored).toBe(false)
    expect(store.count).toBe(1)

    store.showIgnored = true
    expect(store.count).toBe(1)
  })

  it('keeps the choice in this browser and starts from it', async () => {
    const store = useLibraryIssuesStore()
    store.showIgnored = true
    await nextTick()
    expect(window.localStorage.getItem('listenarr.issuesShowIgnored')).toBe('true')

    setActivePinia(createPinia())
    const next = useLibraryIssuesStore()
    await next.load()
    expect(next.showIgnored).toBe(true)
  })
})
