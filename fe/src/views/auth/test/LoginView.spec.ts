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
import { mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter } from 'vue-router'
import LoginView from '@/views/auth/LoginView.vue'
import { flushAsync } from '@/test/utils/wait'

const apiService = vi.hoisted(() => ({
  fetchAntiforgeryToken: vi.fn(),
  getBootstrapConfig: vi.fn(),
}))

const auth = vi.hoisted(() => ({
  login: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  apiService,
}))

vi.mock('@/stores/auth', () => ({
  useAuthStore: () => auth,
}))

const routes = [
  { path: '/', name: 'home', component: { template: '<div />' } },
  { path: '/login', name: 'login', component: LoginView },
  { path: '/settings', name: 'settings', component: { template: '<div />' } },
  { path: '/wanted', name: 'wanted', component: { template: '<div />' } },
]

async function mountLogin(initialPath = '/login') {
  const router = createRouter({
    history: createMemoryHistory(),
    routes,
  })
  await router.push(initialPath)
  await router.isReady()

  const wrapper = mount(LoginView, {
    global: {
      plugins: [router],
    },
  })
  await flushAsync()

  return { router, wrapper }
}

describe('LoginView auth redirects', () => {
  beforeEach(() => {
    apiService.fetchAntiforgeryToken.mockReset()
    apiService.fetchAntiforgeryToken.mockResolvedValue('csrf')
    apiService.getBootstrapConfig.mockReset()
    apiService.getBootstrapConfig.mockResolvedValue({ authenticationRequired: true })
    auth.login.mockReset()
    auth.login.mockResolvedValue(undefined)
    sessionStorage.removeItem('listenarr_pending_redirect')
  })

  it('uses a safe query redirect after successful login', async () => {
    const { router, wrapper } = await mountLogin(
      '/login?redirect=/settings%3Ftab%3Dgeneral%23indexers',
    )

    await (wrapper.vm as unknown as { onSubmit: () => Promise<void> }).onSubmit()

    expect(auth.login).toHaveBeenCalled()
    expect(router.currentRoute.value.path).toBe('/settings')
    expect(router.currentRoute.value.query.tab).toBe('general')
    expect(router.currentRoute.value.hash).toBe('#indexers')
  })

  it('uses the pending session redirect when query redirect is absent', async () => {
    sessionStorage.setItem('listenarr_pending_redirect', '/wanted')
    const { router, wrapper } = await mountLogin('/login')

    await (wrapper.vm as unknown as { onSubmit: () => Promise<void> }).onSubmit()

    expect(router.currentRoute.value.name).toBe('wanted')
  })

  it('falls back home when redirect values are unsafe', async () => {
    sessionStorage.setItem('listenarr_pending_redirect', 'https://evil.example/')
    const { router, wrapper } = await mountLogin('/login?redirect=https%3A%2F%2Fevil.example%2F')

    await (wrapper.vm as unknown as { onSubmit: () => Promise<void> }).onSubmit()

    expect(router.currentRoute.value.name).toBe('home')
  })

  it('redirects away from login when authentication is disabled', async () => {
    apiService.getBootstrapConfig.mockResolvedValue({ authenticationRequired: false })

    const { router } = await mountLogin('/login')
    await flushAsync()

    expect(router.currentRoute.value.name).toBe('home')
  })
})
