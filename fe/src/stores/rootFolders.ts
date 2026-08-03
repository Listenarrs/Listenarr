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
import { rootFolderPathChanged } from '@/utils/rootFolderPath'

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
      folders.value = []
    } finally {
      loading.value = false
    }
  }

  async function create(payload: {
    name: string
    path: string
    isDefault?: boolean
    caseSensitivityMode?: 'Auto' | 'Sensitive' | 'Insensitive'
  }) {
    const r = await apiService.createRootFolder(payload)
    await load()
    return r
  }

  async function update(
    id: number,
    payload: {
      id: number
      name: string
      path: string
      isDefault?: boolean
      caseSensitivityMode?: 'Auto' | 'Sensitive' | 'Insensitive'
    },
    opts?: {
      expectedCurrentPath?: string
      pathChangeConfirmed?: boolean
      moveFiles?: boolean
      deleteEmptySource?: boolean
    },
  ) {
    let current = folders.value.find((folder) => folder.id === id)
    if (!current) {
      await load()
      current = folders.value.find((folder) => folder.id === id)
    }
    if (!current) {
      throw new Error('Root folder changed or was removed; reload and try again')
    }
    if (opts?.expectedCurrentPath != null && current.path !== opts.expectedCurrentPath) {
      throw new Error(
        'Root folder path changed while editing; review the current path and try again',
      )
    }

    const requestedMode = payload.caseSensitivityMode ?? current.caseSensitivityMode ?? 'Auto'
    const hasPathChange = rootFolderPathChanged(current, payload.path)
    let pathChangeError: string | null = null
    if (hasPathChange) {
      if (opts?.pathChangeConfirmed !== true) {
        throw new Error('Root folder path change requires confirmation')
      }

      const result = await apiService.changeRootFolderPath(id, {
        targetPath: payload.path,
        mode: opts?.moveFiles === false ? 'metadataOnly' : 'relocate',
        deleteEmptySource: opts?.deleteEmptySource !== false,
        desiredName: payload.name,
        desiredIsDefault: payload.isDefault === true,
        targetCaseSensitivityMode: requestedMode,
        expectedCurrentPath: current.path,
      })
      if (result.status === 'NeedsAttention' || result.status === 'Failed') {
        pathChangeError =
          result.error ||
          (result.status === 'NeedsAttention'
            ? 'The root folder relocation requires attention.'
            : 'The root folder relocation failed.')
      }
    } else {
      // PATCH intentionally preserves the canonical stored path. Equivalent
      // separator/case spelling is not a relocation or a path representation update.
      await apiService.updateRootFolder(id, { ...payload, path: current.path })
    }
    await load()
    if (pathChangeError) {
      throw new Error(pathChangeError)
    }
    return folders.value.find((folder) => folder.id === id) ?? current!
  }

  async function retryRelocation(relocationId: string) {
    const result = await apiService.retryRootFolderRelocation(relocationId)
    await load()
    return result
  }

  async function reauthorizeLegacyTarget(relocationId: string, confirmedTargetPath: string) {
    const result = await apiService.reauthorizeLegacyRootFolderRelocationTarget(
      relocationId,
      confirmedTargetPath,
    )
    await load()
    return result
  }

  async function remove(id: number, reassignTo?: number) {
    const r = await apiService.deleteRootFolder(id, reassignTo)
    await load()
    return r
  }

  return {
    folders,
    loading,
    defaultFolder,
    load,
    create,
    update,
    retryRelocation,
    reauthorizeLegacyTarget,
    remove,
  }
})
