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
import { onScopeDispose, watch, type Ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'

export interface PageLeaveGuardOptions {
  // Leaving now would stop work that runs in this page
  isActive: Ref<boolean>
  // Asked before going to another page of the app: true to leave
  confirmLeave: () => Promise<boolean>
  // Called once the user chose to leave, before the page goes
  onLeave?: () => void
}

// Work that only runs in this page (in the browser) is asked about before it is left: going to
// another page of the app asks confirmLeave; closing or reloading the tab gets the browser's own
// warning (its wording can't be chosen). Only while isActive. For a component under the page's
// router view (route guards need one).
export function usePageLeaveGuard({ isActive, confirmLeave, onLeave }: PageLeaveGuardOptions) {
  onBeforeRouteLeave(async () => {
    if (!isActive.value) return true
    const isLeaving = await confirmLeave()
    if (isLeaving) onLeave?.()
    return isLeaving
  })

  function warnBeforeUnload(event: BeforeUnloadEvent) {
    event.preventDefault()
    // Older browsers show the warning only with returnValue set
    event.returnValue = ''
  }

  function stopWarning() {
    window.removeEventListener('beforeunload', warnBeforeUnload)
  }

  watch(
    isActive,
    (isGuarding) => {
      if (isGuarding) window.addEventListener('beforeunload', warnBeforeUnload)
      else stopWarning()
    },
    { immediate: true },
  )

  onScopeDispose(stopWarning)
}
