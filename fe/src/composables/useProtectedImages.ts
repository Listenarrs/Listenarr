import { onUnmounted, reactive } from 'vue'
import { apiService } from '@/services/api'
import { getCachedStartupConfig } from '@/services/startupConfigCache'

export function isLikelyBackendImageUrl(url: string): boolean {
  if (!url) return false
  if (url.startsWith('/api/images/')) return true
  if (url.includes('/api/images/')) return true
  if (url.startsWith('/config/cache/images/')) return true
  if (url.includes('/config/cache/images/')) return true
  return false
}

export function useProtectedImages() {
  const protectedImageSrcMap = reactive<Record<string, string>>({})
  const protectedImageLoading = reactive<Record<string, boolean>>({})
  const protectedImageError = reactive<Record<string, boolean>>({})
  const protectedImageSourceMap = reactive<Record<string, string>>({})
  const protectedImageObjectUrls = new Set<string>()

  function revokeProtectedImageUrl(url: string | undefined) {
    if (!url) return
    if (!url.startsWith('blob:')) return
    try {
      URL.revokeObjectURL(url)
    } catch {}
    try {
      protectedImageObjectUrls.delete(url)
    } catch {}
  }

  function resetProtectedImageKey(cacheKey: string) {
    const previous = protectedImageSrcMap[cacheKey]
    if (previous) revokeProtectedImageUrl(previous)
    delete protectedImageSrcMap[cacheKey]
    delete protectedImageLoading[cacheKey]
    delete protectedImageError[cacheKey]
  }

  function clearProtectedImages() {
    for (const objectUrl of Array.from(protectedImageObjectUrls)) {
      revokeProtectedImageUrl(objectUrl)
    }
    for (const key of Object.keys(protectedImageSrcMap)) {
      delete protectedImageSrcMap[key]
    }
    for (const key of Object.keys(protectedImageLoading)) {
      delete protectedImageLoading[key]
    }
    for (const key of Object.keys(protectedImageError)) {
      delete protectedImageError[key]
    }
    for (const key of Object.keys(protectedImageSourceMap)) {
      delete protectedImageSourceMap[key]
    }
  }

  function getProtectedImageSrc(rawImageUrl: string | undefined, cacheKey: string, fallback = ''): string {
    if (!rawImageUrl) return fallback
    const safeKey = (cacheKey || 'default').replace(/[^A-Za-z0-9._-]/g, '_')

    const previousSource = protectedImageSourceMap[safeKey]
    if (previousSource !== rawImageUrl) {
      protectedImageSourceMap[safeKey] = rawImageUrl
      resetProtectedImageKey(safeKey)
    }

    const existing = protectedImageSrcMap[safeKey]
    if (existing) return existing
    if (protectedImageError[safeKey]) {
      const retryResolved = apiService.getImageUrl(rawImageUrl)
      if (!isLikelyBackendImageUrl(retryResolved)) return fallback
    }

    const resolvedImmediate = apiService.getImageUrl(rawImageUrl)
    if (resolvedImmediate) {
      const isBackendImage = isLikelyBackendImageUrl(resolvedImmediate)
      if (!isBackendImage || !isAuthRequiredByConfig()) {
        protectedImageSrcMap[safeKey] = resolvedImmediate
        return resolvedImmediate
      }
    }

    if (!protectedImageLoading[safeKey]) {
      protectedImageLoading[safeKey] = true
      void (async () => {
        try {
          const resolved = apiService.getImageUrl(rawImageUrl)
          if (!resolved) {
            protectedImageError[safeKey] = true
            return
          }

          if (!isLikelyBackendImageUrl(resolved)) {
            protectedImageSrcMap[safeKey] = resolved
            return
          }

          if (!isAuthRequiredByConfig()) {
            // Preserve existing UX for non-auth deployments while still upgrading
            // to an authenticated blob URL when available.
            protectedImageSrcMap[safeKey] = resolved
          }

          if (typeof apiService.fetchImageObjectUrl !== 'function') {
            protectedImageSrcMap[safeKey] = resolved
            return
          }

          const objectUrl = await apiService.fetchImageObjectUrl(rawImageUrl)
          if (!objectUrl) {
            if (!isLikelyBackendImageUrl(resolved) || !isAuthRequiredByConfig()) {
              protectedImageSrcMap[safeKey] = resolved
              delete protectedImageError[safeKey]
            } else {
              protectedImageError[safeKey] = true
            }
            return
          }

          const previous = protectedImageSrcMap[safeKey]
          if (previous && previous !== objectUrl) {
            revokeProtectedImageUrl(previous)
          }

          protectedImageSrcMap[safeKey] = objectUrl
          delete protectedImageError[safeKey]
          if (objectUrl.startsWith('blob:')) {
            protectedImageObjectUrls.add(objectUrl)
          }
        } catch {
          const resolved = apiService.getImageUrl(rawImageUrl)
          if (resolved && isLikelyBackendImageUrl(resolved) && !isAuthRequiredByConfig()) {
            protectedImageSrcMap[safeKey] = resolved
            delete protectedImageError[safeKey]
          } else {
            protectedImageError[safeKey] = true
          }
        } finally {
          protectedImageLoading[safeKey] = false
        }
      })()
    }

    return fallback
  }

  onUnmounted(() => {
    clearProtectedImages()
  })

  return {
    clearProtectedImages,
    getProtectedImageSrc,
    revokeProtectedImageUrl,
  }
}
  function isAuthRequiredByConfig(): boolean {
    try {
      const cfg = getCachedStartupConfig() as Record<string, unknown> | null
      if (!cfg) return false
      const raw = cfg.authenticationRequired ?? cfg.AuthenticationRequired
      if (typeof raw === 'boolean') return raw
      if (typeof raw === 'string') {
        const normalized = raw.trim().toLowerCase()
        return normalized === 'true' || normalized === 'enabled'
      }
      return false
    } catch {
      return false
    }
  }
