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
import { computed, ref } from 'vue'
import { apiService } from '@/services/api'
import { logger } from '@/utils/logger'
import type { LibraryIssue } from '@/types'

// The books whose Audible identity is incomplete, shown under System > Issues with their count
export const useLibraryIssuesStore = defineStore('libraryIssues', () => {
  const items = ref<LibraryIssue[]>([])
  const loading = ref(false)
  const loaded = ref(false)
  const count = computed(() => items.value.length)

  async function load() {
    loading.value = true
    try {
      items.value = await apiService.getLibraryIssues()
      loaded.value = true
    } catch (e) {
      logger.debug('Failed to load library issues', e)
    } finally {
      loading.value = false
    }
  }

  return { items, loading, loaded, count, load }
})
