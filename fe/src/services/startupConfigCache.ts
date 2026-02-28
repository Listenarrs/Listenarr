import { apiService } from './api'
import { logger } from '@/utils/logger'

type StartupConfig = import('@/types').StartupConfig

let _cache: StartupConfig | null = null
let _cacheTs = 0
let _inflight: Promise<StartupConfig | null> | null = null
// Expose a simple counter for diagnostics/tests
export let fetchCount = 0

export async function getStartupConfigCached(ttlMs = 5000): Promise<StartupConfig | null> {
  const now = Date.now()
  // If we have a cached value, check if it's a 401 fallback and use a longer TTL
  const isAuthRequired = _cache && typeof _cache === 'object' && (_cache as any).authenticationRequired === true
  const effectiveTtl = isAuthRequired ? 300000 : ttlMs // 5 minutes for 401, else normal TTL
  if (_cacheTs !== 0 && now - _cacheTs <= effectiveTtl) return _cache

  if (!_inflight) {
    fetchCount++
    _inflight = apiService
      .getStartupConfig()
      .then((cfg) => {
        logger.debug('[startupConfigCache] Raw config response:', cfg)
        _cache = cfg
        _cacheTs = Date.now()
        return cfg
      })
      .catch((err) => {
        logger.debug('[startupConfigCache] Error fetching config:', err)
        // If 401 Unauthorized, treat as 'authentication required' for SPA logic
        const status = (err as { status?: number } | null)?.status
        if (status === 401) {
          const fallback: Partial<StartupConfig> = { authenticationRequired: true }
          _cache = fallback as StartupConfig
          _cacheTs = Date.now()
          return _cache
        }
        // On other errors, cache null result for the TTL
        _cache = null
        _cacheTs = Date.now()
        return null
      })
      .finally(() => {
        _inflight = null
      })
  }

  return _inflight
}

export function resetCache() {
  _cache = null
  _cacheTs = 0
  _inflight = null
  fetchCount = 0
}

// Synchronous access to the cached startup config (may be null)
export function getCachedStartupConfig(): StartupConfig | null {
  return _cache
}
