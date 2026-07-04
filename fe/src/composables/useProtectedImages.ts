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
import { apiService } from '@/services/api'
import { isApiImagesUrl } from '@/services/apiBase'

export function isLikelyBackendImageUrl(url: string): boolean {
  if (!url) return false
  if (isApiImagesUrl(url)) return true
  if (url.startsWith('/config/cache/images/')) return true
  if (url.includes('/config/cache/images/')) return true
  return false
}

export function useProtectedImages() {
  function getProtectedImageSrc(
    rawImageUrl: string | undefined,
    fallback = '',
    opts?: { size?: 'grid' | 'grid2x' },
  ): string {
    if (!rawImageUrl) return fallback
    return apiService.getImageUrl(rawImageUrl, opts) || fallback
  }

  return {
    getProtectedImageSrc,
  }
}
