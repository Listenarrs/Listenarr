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
import { setRouter } from '@/services/routerInstance'
import type { StartupConfig } from '@/types'

// Module-level cache/promise for startup config to avoid repeated requests during rapid navigation
// Use a promise so concurrent navigations share the same inflight request instead of issuing many
// Module-level cache moved to services/startupConfigCache

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

// Preload helper: given a route name or path, trigger the route's lazy component import
// without navigating. Returns the import promise or a resolved promise when not found.
export function preloadRoute(nameOrPath: string) {
  // try by name first
  const byName = routes.find((r) => r.name === nameOrPath)
  if (byName && typeof byName.component === 'function') {
    try {
      return (byName.component as unknown as () => Promise<unknown>)()
    } catch {
      return Promise.resolve()
    }
  }
  // try by path
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

// Factory function to create and configure the router.
// Deferred to avoid calling createWebHistory/createRouter at module top-level,
// which triggers a Rolldown (Vite 8) circular-dependency crash where vue-router
// symbols are not yet initialized when this module is first evaluated.
export function createAppRouter() {
  const router = createRouter({
    history: createWebHistory(import.meta.env.BASE_URL),
    routes,
  })

  // Navigation guard: protect routes requiring auth and preserve redirectTo
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

    // Load current user only once per app lifetime
    if (!auth.loaded) {
      try {
        await auth.loadCurrentUser()
      } catch {}
    }

    // Fetch startup config with a short cache window so rapid navigations do not each
    // block on a network round-trip. Auth settings rarely change mid-session; 30 s is
    // conservative enough to stay in sync while avoiding per-navigation latency.
    const startupConfig = await getStartupConfigCached(30_000)
    const startupConfigMissing = !startupConfig
    logger.debug('[router] startupConfigMissing', startupConfigMissing)
    logger.debug('[router] startupConfig', redactStartupConfigForLog(startupConfig))
    const authRequiredConfig = (() => {
      if (startupConfigMissing) {
        logger.debug('[router] startupConfig missing, defaulting authRequiredConfig to false')
        // If the backend is temporarily unreachable or the config fetch fails,
        // do not force the login screen. Treat missing config as "no auth"
        // to avoid blocking the SPA from loading.
        return false
      }
      const raw =
        startupConfig?.authenticationRequired ??
        (startupConfig as StartupConfig & { AuthenticationRequired?: string | boolean })
          ?.AuthenticationRequired
      logger.debug('[router] startupConfig raw authRequired:', raw)
      const v = raw
      if (v === undefined || v === null) {
        logger.debug('[router] authRequiredConfig: value undefined/null, returning false')
        return false
      }
      if (typeof v === 'boolean') {
        logger.debug('[router] authRequiredConfig: boolean value', v)
        return v
      }
      if (typeof v === 'string') {
        const parsed = v.toLowerCase() === 'enabled' || v.toLowerCase() === 'true'
        logger.debug('[router] authRequiredConfig: string value', v, 'parsed as', parsed)
        return parsed
      }
      logger.debug('[router] authRequiredConfig: unknown type, returning false')
      return false
    })()
    logger.debug('[router] FINAL authRequiredConfig:', authRequiredConfig)

    // If authentication is disabled in startup config, prevent access to login page
    if (!authRequiredConfig) {
      // Authentication globally disabled: don't enforce requiresAuth.
      // Still prevent navigating to the login page when auth is disabled.
      if (to.name === 'login') {
        // Allow explicitly forced login navigation (used right after enabling auth)
        // to avoid race conditions where startup config propagation briefly reports
        // authentication as disabled.
        if (forceLogin && !auth.user.authenticated) {
          logger.debug('[router] force login requested; allowing login route despite auth config')
          return true
        }
        // Check if there's a redirect parameter - if so, honor it instead of going to home
        // Also check auth.redirectTo store as fallback (set during initial navigation attempts)
        const redirectPath = (to.query.redirect as string | undefined) || auth.redirectTo

        if (redirectPath) {
          // Parse and navigate to the intended destination
          try {
            const url = new URL(redirectPath, window.location.origin)
            const dest = {
              path: url.pathname,
              query: Object.fromEntries(url.searchParams),
              hash: url.hash, // Preserve the hash/anchor (e.g., #indexers)
            }
            auth.redirectTo = null
            logger.debug(
              '[router] auth disabled, but redirect found in store/query, going to:',
              dest,
            )
            return dest
          } catch {
            // Fallback to string path
            auth.redirectTo = null
            logger.debug(
              '[router] auth disabled, but redirect found in store/query (fallback), going to:',
              redirectPath,
            )
            return redirectPath
          }
        }

        // No redirect - go to home
        logger.debug('[router] auth disabled, no redirect found, going to home')
        return { name: 'home' }
      }

      // Auth disabled: allow all other routes through without checking authentication
      // But still preserve the intended destination if user tried to access login somehow
      if (!auth.loaded && to.meta.requiresAuth) {
        // First time loading a protected route - save it before auth finishes loading
        auth.redirectTo = to.fullPath
        logger.debug('[router] auth disabled, saving redirect for initial load:', to.fullPath)
      }
    } else {
      // Authentication enabled: enforce protection on routes marked as requiresAuth
      if ((to.meta as Record<string, unknown>)?.requiresAuth && !auth.user.authenticated) {
        // Preserve the intended route and redirect to login
        // Use a query param so the redirect survives page reloads; also keep store as fallback
        auth.redirectTo = to.fullPath
        logger.debug('[router] requiresAuth and not authenticated, redirecting to login', {
          redirect: to.fullPath,
        })
        return { name: 'login', query: { redirect: to.fullPath } }
      }
    }

    // If already authenticated and going to login, redirect to saved destination
    if (to.name === 'login' && auth.user.authenticated) {
      // Check for redirect in query params first (survives page reloads), then fall back to store
      const redirectPath = (to.query.redirect as string | undefined) || auth.redirectTo

      if (redirectPath) {
        // Parse the redirect path to extract path, query, and hash components
        // This ensures we properly handle anchors like /settings#indexers
        try {
          const url = new URL(redirectPath, window.location.origin)
          const dest = {
            path: url.pathname,
            query: Object.fromEntries(url.searchParams),
            hash: url.hash, // Preserve the hash/anchor (e.g., #indexers)
          }
          auth.redirectTo = null
          logger.debug('[router] authenticated user on login page, redirecting to:', dest)
          return dest
        } catch {
          // Fallback: if URL parsing fails, use the path string directly
          // Vue Router should still handle it correctly
          auth.redirectTo = null
          logger.debug(
            '[router] authenticated user on login page, redirecting to (fallback):',
            redirectPath,
          )
          return redirectPath
        }
      }

      // No redirect path - go to home
      auth.redirectTo = null
      return { name: 'home' }
    }

    return true
  })

  setRouter(router)
  return router
}
