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
import { createPinia, setActivePinia } from 'pinia'

const apiMocks = vi.hoisted(() => ({
  getCurrentUser: vi.fn(),
  login: vi.fn(),
  logout: vi.fn(),
}))

const clearAllAuthData = vi.hoisted(() => vi.fn())
const captureException = vi.hoisted(() => vi.fn())

vi.mock('@/services/api', () => ({
  apiService: {
    getCurrentUser: apiMocks.getCurrentUser,
    login: apiMocks.login,
    logout: apiMocks.logout,
  },
}))

vi.mock('@/utils/sessionDebug', () => ({
  clearAllAuthData,
}))

vi.mock('@/services/errorTracking', () => ({
  errorTracking: {
    captureException,
  },
}))

const resetStorage = () => {
  localStorage.removeItem('listenarr_session_token')
  localStorage.removeItem('listenarr_session_token_persistence')
  sessionStorage.removeItem('listenarr_session_token')
}

describe('auth store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    apiMocks.getCurrentUser.mockReset()
    apiMocks.getCurrentUser.mockResolvedValue({ authenticated: false })
    apiMocks.login.mockReset()
    apiMocks.logout.mockReset()
    clearAllAuthData.mockReset()
    captureException.mockReset()
    resetStorage()
  })

  it('loads the current user when another tab broadcasts a login marker', async () => {
    apiMocks.getCurrentUser.mockResolvedValue({ authenticated: true, name: 'cross-tab-user' })

    const { useAuthStore } = await import('@/stores/auth')
    const { sessionTokenManager } = await import('@/utils/sessionToken')
    const store = useAuthStore()

    sessionTokenManager.setToken('authenticated')

    await vi.waitFor(() => {
      expect(apiMocks.getCurrentUser).toHaveBeenCalledTimes(1)
      expect(store.user).toEqual({ authenticated: true, name: 'cross-tab-user' })
    })
  })

  it('clears auth state on cross-tab logout without needing a router', async () => {
    const { useAuthStore } = await import('@/stores/auth')
    const { sessionTokenManager } = await import('@/utils/sessionToken')
    const store = useAuthStore()

    store.user = { authenticated: true, name: 'cross-tab-user' }
    store.loaded = true

    sessionTokenManager.setToken('authenticated')
    sessionTokenManager.clearToken()

    await vi.waitFor(() => {
      expect(store.user.authenticated).toBe(false)
      expect(store.loaded).toBe(true)
    })
    expect(apiMocks.getCurrentUser).not.toHaveBeenCalled()
  })

  it('does not load the current user on initial empty auth marker state', async () => {
    const { useAuthStore } = await import('@/stores/auth')
    const store = useAuthStore()

    await Promise.resolve()

    expect(store.loaded).toBe(false)
    expect(apiMocks.getCurrentUser).not.toHaveBeenCalled()
  })

  it('clears a stale browser auth marker when /account/me reports unauthenticated', async () => {
    apiMocks.getCurrentUser.mockResolvedValue({ authenticated: false })

    const { useAuthStore } = await import('@/stores/auth')
    const { sessionTokenManager } = await import('@/utils/sessionToken')
    const store = useAuthStore()
    sessionTokenManager.setToken('authenticated')

    await store.loadCurrentUser()

    expect(store.user.authenticated).toBe(false)
    expect(sessionTokenManager.getToken()).toBeNull()
  })

  it('clears local auth data when logout fails', async () => {
    apiMocks.logout.mockRejectedValue(new Error('logout failed'))

    const { useAuthStore } = await import('@/stores/auth')
    const { sessionTokenManager } = await import('@/utils/sessionToken')
    const store = useAuthStore()
    sessionTokenManager.setToken('authenticated')
    store.user = { authenticated: true, name: 'user' }

    await store.logout()

    expect(captureException).toHaveBeenCalled()
    expect(clearAllAuthData).toHaveBeenCalled()
    expect(sessionTokenManager.getToken()).toBeNull()
    expect(store.user.authenticated).toBe(false)
    expect(store.loaded).toBe(true)
  })
})
