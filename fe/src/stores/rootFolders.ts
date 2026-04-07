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
      folders.value = []
    } finally {
      loading.value = false
    }
  }

  async function create(payload: { name: string; path: string; isDefault?: boolean;audiobookshelfLibraryId?: string | null }) {
    const r = await apiService.createRootFolder(payload)
    await load()
    return r
  }

  async function update(
    id: number,
    payload: { id: number; name: string; path: string; isDefault?: boolean;audiobookshelfLibraryId?: string | null },
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
