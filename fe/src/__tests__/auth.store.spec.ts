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
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { sessionTokenManager } from '@/utils/sessionToken'

const getCurrentUserMock = vi.fn()
const loginMock = vi.fn()
const logoutMock = vi.fn()
const getStartupConfigCachedMock = vi.fn()

vi.mock('@/services/api', () => ({
  apiService: {
    getCurrentUser: (...args: unknown[]) => getCurrentUserMock(...args),
    login: (...args: unknown[]) => loginMock(...args),
    logout: (...args: unknown[]) => logoutMock(...args),
  },
}))

vi.mock('@/utils/sessionDebug', () => ({
  clearAllAuthData: vi.fn(),
}))

vi.mock('@/services/errorTracking', () => ({
  errorTracking: {
    captureException: vi.fn(),
  },
}))

vi.mock('@/services/startupConfigCache', () => ({
  getStartupConfigCached: (...args: unknown[]) => getStartupConfigCachedMock(...args),
}))

import { useAuthStore } from '@/stores/auth'
import { createAppRouter } from '@/router'

const router = createAppRouter()

describe('auth store cross-tab sync', () => {
  beforeEach(async () => {
    setActivePinia(createPinia())
    getCurrentUserMock.mockReset()
    getCurrentUserMock.mockResolvedValue({ authenticated: false })
    loginMock.mockReset()
    logoutMock.mockReset()
    getStartupConfigCachedMock.mockReset()
    getStartupConfigCachedMock.mockResolvedValue({ authenticationRequired: true })
    sessionTokenManager.clearToken()
    localStorage.removeItem('listenarr_session_token')
    localStorage.removeItem('listenarr_session_event')
    localStorage.removeItem('listenarr_session_token_persistence')
    sessionStorage.removeItem('listenarr_session_token')
    window.history.replaceState({}, '', '/login')
    await router.replace('/login')
    getCurrentUserMock.mockClear()
  })

  afterEach(async () => {
    sessionTokenManager.clearToken()
    localStorage.removeItem('listenarr_session_token')
    localStorage.removeItem('listenarr_session_event')
    localStorage.removeItem('listenarr_session_token_persistence')
    sessionStorage.removeItem('listenarr_session_token')
    window.history.replaceState({}, '', '/')
    await router.replace('/')
  })

  it('loads the current user when another tab broadcasts a login event', async () => {
    getCurrentUserMock.mockResolvedValue({ authenticated: true, name: 'cross-tab-user' })

    const store = useAuthStore()

    const loginEvent = new Event('storage')
    Object.defineProperty(loginEvent, 'key', { value: 'listenarr_session_event' })
    Object.defineProperty(loginEvent, 'newValue', {
      value: JSON.stringify({ authenticated: true, at: Date.now() }),
    })

    window.dispatchEvent(loginEvent)

    await vi.waitFor(() => {
      expect(getCurrentUserMock).toHaveBeenCalledTimes(1)
      expect(store.user.authenticated).toBe(true)
      expect(store.user.name).toBe('cross-tab-user')
    })
  })

  it('does not redirect on initial empty auth marker state', async () => {
    setActivePinia(createPinia())
    const store = useAuthStore()

    await Promise.resolve()

    expect(router.currentRoute.value.name).toBe('login')
    expect(store.loaded).toBe(false)
    expect(getCurrentUserMock).not.toHaveBeenCalled()
  })

  it('redirects to login when another tab broadcasts a logout event while auth is required', async () => {
    const store = useAuthStore()
    store.user = { authenticated: true, name: 'cross-tab-user' }
    store.loaded = true // prevent router guard from re-calling loadCurrentUser() and resetting auth state
    await router.replace('/settings')

    const logoutEvent = new Event('storage')
    Object.defineProperty(logoutEvent, 'key', { value: 'listenarr_session_event' })
    Object.defineProperty(logoutEvent, 'newValue', {
      value: JSON.stringify({ authenticated: false, at: Date.now() }),
    })

    window.dispatchEvent(logoutEvent)

    await vi.waitFor(() => {
      expect(router.currentRoute.value.name).toBe('login')
      expect(router.currentRoute.value.query.redirect).toBe('/settings')
      expect(store.user.authenticated).toBe(false)
    })
  })

  it('clears a stale browser auth marker when /account/me reports unauthenticated', async () => {
    getCurrentUserMock.mockResolvedValue({ authenticated: false })

    const store = useAuthStore()
    sessionTokenManager.setAuthenticated()

    await vi.waitFor(() => {
      expect(store.user.authenticated).toBe(false)
      expect(sessionTokenManager.getToken()).toBeNull()
    })
  })
})
