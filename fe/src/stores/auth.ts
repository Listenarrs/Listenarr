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
import { ref } from 'vue'
import { apiService } from '@/services/api'
import { sessionTokenManager } from '@/utils/sessionToken'
import { clearAllAuthData } from '@/utils/sessionDebug'
import { errorTracking } from '@/services/errorTracking'

export const useAuthStore = defineStore('auth', () => {
  const user = ref<{ authenticated: boolean; name?: string }>({ authenticated: false })
  // Whether we've attempted to load the current user at least once
  const loaded = ref<boolean>(false)
  let currentUserLoadPromise: Promise<void> | null = null

  const loadCurrentUser = async () => {
    if (currentUserLoadPromise) {
      return currentUserLoadPromise
    }

    currentUserLoadPromise = (async () => {
      console.log('[AuthStore] Loading current user...')
      try {
        const u = await apiService.getCurrentUser()
        console.log('[AuthStore] Current user loaded:', u)
        user.value = u
        loaded.value = true

        if (!u.authenticated && sessionTokenManager.hasToken()) {
          console.log(
            '[AuthStore] Clearing stale browser auth marker after unauthenticated /account/me response',
          )
          sessionTokenManager.clearToken()
        }
      } catch (error) {
        console.warn('[AuthStore] Failed to load current user:', error)
        const status =
          error && typeof error === 'object' && 'status' in error
            ? ((error as unknown as { status?: number }).status ?? 0)
            : 0
        if (status === 401 || status === 403) {
          console.log('[AuthStore] Authentication error - clearing session')
          try {
            if (sessionTokenManager.hasToken()) {
              sessionTokenManager.clearToken()
            }
          } catch {}
        }
        user.value = { authenticated: false }
        loaded.value = true
      } finally {
        currentUserLoadPromise = null
      }
    })()

    return currentUserLoadPromise
  }

  const login = async (
    username: string,
    password: string,
    rememberMe: boolean,
    csrfToken?: string,
  ) => {
    await apiService.login(username, password, rememberMe, csrfToken)
    await loadCurrentUser()
  }

  // React to browser auth marker changes from other tabs (cross-tab login/logout).
  try {
    sessionTokenManager.onTokenChange((token, context) => {
      if (context?.source === 'initial') {
        return
      }

      if (token) {
        if (!user.value.authenticated) {
          void loadCurrentUser()
        }
        return
      }

      console.log('[AuthStore] Auth marker removed in another tab - clearing auth state')
      user.value = { authenticated: false }
      loaded.value = true
    })
  } catch {}

  const clearClientAuthState = () => {
    try {
      sessionTokenManager.clearToken()
    } catch (e) {
      console.warn('[AuthStore] Failed to clear sessionTokenManager:', e)
    }

    try {
      // Comprehensive cleanup (removes any lingering storage keys)
      clearAllAuthData()
    } catch (e) {
      console.warn('[AuthStore] Failed to run clearAllAuthData:', e)
    }

    user.value = { authenticated: false }
    loaded.value = true
  }

  const logout = async () => {
    try {
      console.log('[AuthStore] Starting logout...')
      await apiService.logout()
      console.log('[AuthStore] Logout API call successful')
    } catch (error) {
      errorTracking.captureException(error as Error, {
        component: 'AuthStore',
        operation: 'logout',
      })
      // Continue with local logout even if API call fails
    } finally {
      // Ensure all client-side auth data is cleared even if the API call failed
      clearClientAuthState()
      console.log('[AuthStore] Local user state cleared and auth data removed')
    }
  }

  return { user, loadCurrentUser, login, logout, loaded }
})
