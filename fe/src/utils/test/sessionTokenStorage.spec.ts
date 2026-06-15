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
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { installStorageMock } from '@/test/utils/storage'
import { sessionTokenManager } from '@/utils/sessionToken'

describe('sessionTokenManager storage propagation', () => {
  beforeEach(() => {
    vi.resetModules()
    installStorageMock()
    // Ensure clean browser auth state before each test.
    try {
      sessionTokenManager.clearToken()
      localStorage.removeItem('listenarr_session_token')
      localStorage.removeItem('listenarr_session_event')
      localStorage.removeItem('listenarr_session_token_persistence')
      sessionStorage.removeItem('listenarr_session_token')
    } catch {}
  })

  afterEach(() => {
    try {
      sessionTokenManager.clearToken()
      localStorage.removeItem('listenarr_session_token')
      localStorage.removeItem('listenarr_session_event')
      localStorage.removeItem('listenarr_session_token_persistence')
      sessionStorage.removeItem('listenarr_session_token')
    } catch {}
  })

  it('notifies subscribers when storage is changed (cross-tab)', async () => {
    const { sessionTokenManager } = await import('@/utils/sessionToken')
    const events: Array<string | null> = []
    const unsub = sessionTokenManager.onTokenChange((token) => {
      events.push(token)
    })

    // Simulate another tab writing the browser auth marker to localStorage.
    const token = 'cookie-session'
    // Manually set localStorage (emulating other tab)
    localStorage.setItem('listenarr_session_token', token)
    // Emit storage event
    const ev = new Event('storage')
    Object.defineProperty(ev, 'key', { value: 'listenarr_session_token' })
    Object.defineProperty(ev, 'newValue', { value: token })
    window.dispatchEvent(ev)

    expect(events.length).toBeGreaterThanOrEqual(1)
    expect(events[events.length - 1]).toBe(token)

    // Now simulate removal
    localStorage.removeItem('listenarr_session_token')
    const ev2 = new Event('storage')
    Object.defineProperty(ev2, 'key', { value: 'listenarr_session_token' })
    Object.defineProperty(ev2, 'newValue', { value: null })
    window.dispatchEvent(ev2)

    expect(events[events.length - 1]).toBe(null)

    unsub()
  })

  it('sanitizes stored browser auth state so bearer secrets are not persisted', () => {
    sessionTokenManager.setToken('real-session-token', { persistent: true })

    expect(sessionTokenManager.getToken()).toBe('cookie-session')
    expect(localStorage.getItem('listenarr_session_token')).toBe('cookie-session')
  })

  it('reacts to cross-tab auth event broadcasts without storing a secret', () => {
    const events: Array<string | null> = []
    const unsub = sessionTokenManager.onTokenChange((token) => {
      events.push(token)
    })

    const loginEvent = new Event('storage')
    Object.defineProperty(loginEvent, 'key', { value: 'listenarr_session_event' })
    Object.defineProperty(loginEvent, 'newValue', {
      value: JSON.stringify({ authenticated: true, at: Date.now() }),
    })
    window.dispatchEvent(loginEvent)

    expect(events[events.length - 1]).toBe('cookie-session')

    const logoutEvent = new Event('storage')
    Object.defineProperty(logoutEvent, 'key', { value: 'listenarr_session_event' })
    Object.defineProperty(logoutEvent, 'newValue', {
      value: JSON.stringify({ authenticated: false, at: Date.now() + 1 }),
    })
    window.dispatchEvent(logoutEvent)

    expect(events[events.length - 1]).toBe(null)

    unsub()
  })
})
