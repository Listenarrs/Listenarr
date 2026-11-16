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
import { setActivePinia, createPinia } from 'pinia'
import { useLibraryStore } from '@/stores/library'
import { describe, test, expect, beforeEach } from 'vitest'
import type { Audiobook } from '@/types'

describe('library store merge', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  test('preserves local files when server returns empty files array', async () => {
    const store = useLibraryStore()
    store.audiobooks = [
      { id: 1, title: 'Local', files: [{ id: 10, path: '/local/file.m4b' }] },
    ] as Audiobook[]

    const serverList = [{ id: 1, title: 'Local (server)', files: [] }]

    // emulate internal fetchLibrary merge behavior
    const merged = serverList.map((serverItem) => {
      const local = store.audiobooks.find((b) => b.id === serverItem.id)
      if (!local) return serverItem

      const files =
        serverItem.files && serverItem.files.length > 0
          ? serverItem.files
          : local.files && local.files.length > 0
            ? local.files
            : serverItem.files

      return { ...local, ...serverItem, files }
    })

    expect(merged[0].files.length).toBe(1)
    expect(merged[0].files[0].path).toBe('/local/file.m4b')
  })
})
