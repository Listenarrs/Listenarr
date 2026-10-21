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
import { computed, ref, watch } from 'vue'
import { apiService } from '@/services/api'
import { logger } from '@/utils/logger'
import type { LibraryIssue } from '@/types'

// Whether the Issues page lists ignored books, kept in this browser like the library's display
// choices
const SHOW_IGNORED_KEY = 'listenarr.issuesShowIgnored'

function readShowIgnored(): boolean {
  try {
    return localStorage.getItem(SHOW_IGNORED_KEY) === 'true'
  } catch {
    return false
  }
}

// The books whose Audible identity is incomplete, shown in Issues (under System) with their count.
// Ignored books are kept too: they are listed on demand.
export const useLibraryIssuesStore = defineStore('libraryIssues', () => {
  const items = ref<LibraryIssue[]>([])
  const loading = ref(false)
  const loaded = ref(false)
  const active = computed(() => items.value.filter((i) => !i.ignored))
  const ignored = computed(() => items.value.filter((i) => i.ignored))
  const showIgnored = ref(readShowIgnored())
  watch(showIgnored, (v) => {
    try {
      localStorage.setItem(SHOW_IGNORED_KEY, v ? 'true' : 'false')
    } catch {}
  })
  // The orange count: real issues only, ignored books never count (whether they are listed or not)
  const count = computed(() => active.value.length)

  async function setIgnored(id: number, value: boolean) {
    await apiService.setLibraryIssueIgnored(id, value)
    items.value = items.value.map((i) => (i.id === id ? { ...i, ignored: value } : i))
  }

  async function load() {
    loading.value = true
    try {
      items.value = await apiService.getLibraryIssues(true)
      loaded.value = true
    } catch (e) {
      logger.debug('Failed to load library issues', e)
    } finally {
      loading.value = false
    }
  }

  return {
    items,
    active,
    ignored,
    showIgnored,
    loading,
    loaded,
    count,
    load,
    setIgnored,
  }
})
