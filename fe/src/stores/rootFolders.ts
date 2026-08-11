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
  let loadGeneration = 0

  const defaultFolder = computed(() => folders.value.find((f) => f.isDefault) || null)

  async function load() {
    const generation = ++loadGeneration
    loading.value = true
    try {
      const nextFolders =
        typeof apiService.getRootFolders === 'function' ? await apiService.getRootFolders() : []
      if (generation === loadGeneration) {
        folders.value = nextFolders
      }
    } catch (err) {
      logger.debug('Failed to load root folders:', err)
      if (generation === loadGeneration) {
        folders.value = []
      }
    } finally {
      if (generation === loadGeneration) {
        loading.value = false
      }
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
    const hasSemanticsChange = requestedMode !== (current.caseSensitivityMode ?? 'Auto')
    const requiresStorageSemanticsRepair =
      current.storageReason === 'FilesystemSemanticsChanged' ||
      current.storageReason === 'FilesystemSemanticsUnavailable'
    let pathChangeError: string | null = null
    if (hasPathChange || hasSemanticsChange || requiresStorageSemanticsRepair) {
      if ((hasPathChange || requiresStorageSemanticsRepair) && opts?.pathChangeConfirmed !== true) {
        throw new Error('Root folder storage change requires confirmation')
      }

      const pathChangeMode =
        hasPathChange && opts?.moveFiles !== false ? 'relocate' : 'metadataOnly'
      const result = await apiService.changeRootFolderPath(id, {
        targetPath: hasPathChange ? payload.path : current.path,
        mode: pathChangeMode,
        deleteEmptySource: hasPathChange && opts?.deleteEmptySource !== false,
        desiredName: payload.name,
        desiredIsDefault: payload.isDefault === true,
        targetCaseSensitivityMode: requestedMode,
        expectedCurrentPath: current.path,
      })
      if (
        result.status === 'Failed' ||
        (result.status === 'NeedsAttention' && pathChangeMode !== 'metadataOnly')
      ) {
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

  async function confirmCurrentFolder(
    id: number,
    expectedCurrentPath: string,
    confirmationToken: string,
  ) {
    const result = await apiService.confirmRootFolder(id, expectedCurrentPath, confirmationToken)
    await load()
    return result
  }

  async function abandonUnpublishedRelocation(relocationId: string) {
    const result = await apiService.abandonUnpublishedRootFolderRelocation(relocationId)
    await load()
    return result
  }

  async function retryRelocation(relocationId: string) {
    const result = await apiService.retryRootFolderRelocation(relocationId)
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
    confirmCurrentFolder,
    abandonUnpublishedRelocation,
    retryRelocation,
    remove,
  }
})
