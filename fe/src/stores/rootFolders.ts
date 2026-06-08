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
import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { apiService } from '@/services/api'
import { logger } from '@/utils/logger'
import type { RootFolder } from '@/types'

export const useRootFoldersStore = defineStore('rootFolders', () => {
  const folders = ref<RootFolder[]>([])
  const loading = ref(false)

  const defaultFolder = computed(() => folders.value.find((f) => f.isDefault) || null)

  async function load() {
    loading.value = true
    try {
      if (typeof apiService.getRootFolders === 'function') {
        folders.value = await apiService.getRootFolders()
      } else {
        // In some tests the apiService is mocked partially; default to empty list
        folders.value = []
      }
    } catch (err) {
      logger.debug('Failed to load root folders:', err)
      // Preserve any previously-loaded folders on transient failure: a failed
      // reload must not masquerade as "no root folders configured" (folders
      // starts as [], so a first-ever failure still yields an empty list).
    } finally {
      loading.value = false
    }
  }

  async function create(payload: { name: string; path: string; isDefault?: boolean }) {
    const r = await apiService.createRootFolder(payload)
    await load()
    return r
  }

  async function update(
    id: number,
    payload: { id: number; name: string; path: string; isDefault?: boolean },
    opts?: { moveFiles?: boolean; deleteEmptySource?: boolean },
  ) {
    const r = await apiService.updateRootFolder(id, payload, opts)
    await load()
    return r
  }

  async function remove(id: number, reassignTo?: number) {
    const r = await apiService.deleteRootFolder(id, reassignTo)
    await load()
    return r
  }

  return { folders, loading, defaultFolder, load, create, update, remove }
})
