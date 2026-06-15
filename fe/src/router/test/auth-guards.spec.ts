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

const auth = vi.hoisted(() => ({
  user: { authenticated: false },
  loaded: true,
  loadCurrentUser: vi.fn(async () => undefined),
}))

const getStartupConfigCached = vi.hoisted(() => vi.fn())

vi.mock('@/stores/auth', () => ({
  useAuthStore: () => auth,
}))

vi.mock('@/services/startupConfigCache', () => ({
  getStartupConfigCached,
}))

vi.mock('@/utils/logger', () => ({
  logger: {
    log: vi.fn(),
    debug: vi.fn(),
  },
}))

async function loadRouter() {
  vi.resetModules()
  const { createAppRouter } = await import('@/router')
  return createAppRouter()
}

describe('router auth guards', () => {
  beforeEach(() => {
    auth.user.authenticated = false
    auth.loaded = true
    auth.loadCurrentUser.mockClear()
    getStartupConfigCached.mockReset()
    getStartupConfigCached.mockResolvedValue({ authenticationRequired: true })
    window.history.replaceState({}, '', '/')
  })

  it('redirects unauthenticated protected routes to login with the target preserved', async () => {
    const router = await loadRouter()

    await router.push('/settings')
    await router.isReady()

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/settings')
  })

  it('allows protected routes when authentication is disabled', async () => {
    getStartupConfigCached.mockResolvedValue({ authenticationRequired: false })
    const router = await loadRouter()

    await router.push('/settings')
    await router.isReady()

    expect(router.currentRoute.value.name).toBe('settings')
  })

  it('redirects login to home when auth is disabled unless force login is requested', async () => {
    getStartupConfigCached.mockResolvedValue({ authenticationRequired: false })
    let router = await loadRouter()

    await router.push('/login')
    await router.isReady()
    expect(router.currentRoute.value.name).toBe('home')

    router = await loadRouter()
    await router.push('/login?force=1')
    await router.isReady()
    expect(router.currentRoute.value.name).toBe('login')
  })

  it('redirects authenticated login visits to a safe redirect target', async () => {
    auth.user.authenticated = true
    const router = await loadRouter()

    await router.push('/login?redirect=/settings%3Ftab%3Dgeneral%23indexers')
    await router.isReady()

    expect(router.currentRoute.value.name).toBe('settings')
    expect(router.currentRoute.value.query.tab).toBe('general')
    expect(router.currentRoute.value.hash).toBe('#indexers')
  })

  it('falls back home for unsafe login redirect values', async () => {
    auth.user.authenticated = true
    const router = await loadRouter()

    await router.push('/login?redirect=https%3A%2F%2Fevil.example%2F')
    await router.isReady()

    expect(router.currentRoute.value.name).toBe('home')
  })

  it('treats missing startup config as auth disabled for routing fallback', async () => {
    getStartupConfigCached.mockResolvedValue(null)
    const router = await loadRouter()

    await router.push('/settings')
    await router.isReady()

    expect(router.currentRoute.value.name).toBe('settings')
  })
})
