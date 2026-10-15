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

const advancedSearch = vi.hoisted(() => vi.fn())
vi.mock('@/services/api', () => ({ apiService: { advancedSearch } }))

import { findRecords } from '@/utils/audibleRecordSearch'

describe('findRecords', () => {
  beforeEach(() => {
    advancedSearch.mockReset()
  })

  it('searches the title and author in one region, drops empty stubs and tags the region', async () => {
    advancedSearch.mockResolvedValue([
      { title: 'Primal Hunter 4', asin: 'B0BK9W82PQ', authors: [] },
      { asin: 'B0ZZZZZZZZ' },
    ])

    const results = await findRecords({ title: ' The Primal Hunter 4 ', author: 'Zogarth' }, 'us')

    expect(advancedSearch).toHaveBeenCalledWith({
      title: 'The Primal Hunter 4',
      author: 'Zogarth',
      region: 'us',
      cap: 8,
    })
    expect(results).toEqual([expect.objectContaining({ asin: 'B0BK9W82PQ', region: 'us' })])
  })

  it('looks an ASIN up as such, without the author', async () => {
    advancedSearch.mockResolvedValue([])

    await findRecords({ asin: 'b0bk9w82pq', title: 'ignored', author: 'ignored' }, 'fr')

    expect(advancedSearch).toHaveBeenCalledWith({ asin: 'B0BK9W82PQ', region: 'fr', cap: 5 })
  })

  it('searches nothing for an empty query', async () => {
    expect(await findRecords({ title: '  ' }, 'fr')).toEqual([])
    expect(advancedSearch).not.toHaveBeenCalled()
  })
})
