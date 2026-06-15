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
import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { getStartupConfigCached } from '@/services/startupConfigCache'
import { logger } from '@/utils/logger'
import type { StartupConfig } from '@/types'
import { parseAuthRequiredFromConfig } from '@/utils/authConfig'
import { normalizeRedirect, redirectLocationFromPath } from '@/utils/redirect'

const routes = [
  {
    path: '/',
    name: 'home',
    component: () => import('../views/library/AudiobooksView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/audiobooks',
    name: 'audiobooks',
    component: () => import('../views/library/AudiobooksView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/audiobooks/:id',
    name: 'audiobook-detail',
    component: () => import('../views/library/AudiobookDetailView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/collection/:type/:name',
    name: 'collection',
    component: () => import('../views/library/CollectionView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/add-new',
    name: 'add-new',
    component: () => import('../views/content/AddNewView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/library-import',
    name: 'library-import',
    component: () => import('../views/library/LibraryImportView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/activity',
    name: 'activity',
    component: () => import('../views/activity/ActivityView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/wanted',
    name: 'wanted',
    component: () => import('../views/content/WantedView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/calendar',
    name: 'calendar',
    component: () => import('../views/content/CalendarView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/downloads',
    name: 'downloads',
    component: () => import('../views/activity/DownloadsView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/settings',
    name: 'settings',
    component: () => import('../views/SettingsView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/system',
    name: 'system',
    component: () => import('../views/system/SystemView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/logs',
    name: 'logs',
    component: () => import('../views/activity/LogsView.vue'),
    meta: { requiresAuth: true },
  },
  {
    path: '/login',
    name: 'login',
    component: () => import('../views/auth/LoginView.vue'),
    meta: { hideLayout: true },
  },
]

const redactStartupConfigForLog = (
  config: StartupConfig | null,
): StartupConfig | Record<string, unknown> | null => {
  if (!config || typeof config !== 'object') return config
  const cloned = { ...(config as Record<string, unknown>) }
  if (typeof cloned.apiKey === 'string' && cloned.apiKey.length > 0) cloned.apiKey = 'redacted'
  if (typeof cloned.ApiKey === 'string' && cloned.ApiKey.length > 0) cloned.ApiKey = 'redacted'
  return cloned
}

export function preloadRoute(nameOrPath: string) {
  const byName = routes.find((r) => r.name === nameOrPath)
  if (byName && typeof byName.component === 'function') {
    try {
      return (byName.component as unknown as () => Promise<unknown>)()
    } catch {
      return Promise.resolve()
    }
  }

  const byPath = routes.find(
    (r) =>
      r.path === nameOrPath ||
      r.path === (nameOrPath.startsWith('/') ? nameOrPath : `/${nameOrPath}`),
  )
  if (byPath && typeof byPath.component === 'function') {
    try {
      return (byPath.component as unknown as () => Promise<unknown>)()
    } catch {
      return Promise.resolve()
    }
  }

  return Promise.resolve()
}

const getQueryString = (value: unknown): string | undefined =>
  typeof value === 'string' ? value : undefined

export function createAppRouter() {
  const router = createRouter({
    history: createWebHistory(import.meta.env.BASE_URL),
    routes,
  })

  router.beforeEach(async (to, from) => {
    if (import.meta.env.CYPRESS) return true
    const auth = useAuthStore()
    const forceLogin =
      to.name === 'login' &&
      ((to.query.force as string | undefined) === '1' ||
        (to.query.force as string | undefined) === 'true')

    logger.log('router', 'Navigation:', {
      from: from.fullPath,
      to: to.fullPath,
      authenticated: auth.user.authenticated,
      loaded: auth.loaded,
    })

    if (!auth.loaded) {
      try {
        await auth.loadCurrentUser()
      } catch {}
    }

    const startupConfig = await getStartupConfigCached(30_000)
    const startupConfigMissing = !startupConfig
    logger.debug('[router] startupConfigMissing', startupConfigMissing)
    logger.debug('[router] startupConfig', redactStartupConfigForLog(startupConfig))
    const authRequiredConfig = startupConfigMissing
      ? false
      : (parseAuthRequiredFromConfig(startupConfig) ?? false)
    logger.debug('[router] FINAL authRequiredConfig:', authRequiredConfig)

    if (!authRequiredConfig) {
      if (to.name === 'login') {
        if (forceLogin && !auth.user.authenticated) {
          logger.debug('[router] force login requested; allowing login route despite auth config')
          return true
        }

        const redirectPath = normalizeRedirect(getQueryString(to.query.redirect))
        if (redirectPath !== '/') {
          const dest = redirectLocationFromPath(redirectPath)
          logger.debug('[router] auth disabled, but redirect found in query, going to:', dest)
          return dest
        }

        logger.debug('[router] auth disabled, no redirect found, going to home')
        return { name: 'home' }
      }
    } else if ((to.meta as Record<string, unknown>)?.requiresAuth && !auth.user.authenticated) {
      logger.debug('[router] requiresAuth and not authenticated, redirecting to login', {
        redirect: to.fullPath,
      })
      return { name: 'login', query: { redirect: to.fullPath } }
    }

    if (to.name === 'login' && auth.user.authenticated) {
      const redirectPath = normalizeRedirect(getQueryString(to.query.redirect))
      if (redirectPath !== '/') {
        const dest = redirectLocationFromPath(redirectPath)
        logger.debug('[router] authenticated user on login page, redirecting to:', dest)
        return dest
      }

      return { name: 'home' }
    }

    return true
  })

  return router
}
