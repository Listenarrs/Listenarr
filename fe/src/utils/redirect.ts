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
// Small helper to validate/sanitize redirect targets for login flow
export function isSafeRedirect(path: string | undefined | null): boolean {
  if (!path) return false
  // Must be a path that starts with a single slash (no protocol or host)
  if (!path.startsWith('/')) return false
  // Disallow network-path references that start with //
  if (path.startsWith('//')) return false
  // Disallow protocol-looking strings
  if (path.includes('://')) return false
  // Prevent CRLF injection
  if (/\r|\n/.test(path)) return false
  // Keep it short-ish to avoid overly long injection attempts
  if (path.length > 2000) return false
  return true
}

export function normalizeRedirect(path: string | undefined | null): string {
  return isSafeRedirect(path) ? (path as string) : '/'
}

export function redirectLocationFromPath(path: string | undefined | null) {
  const safeRedirect = normalizeRedirect(path)
  if (safeRedirect === '/') {
    return { name: 'home' }
  }

  try {
    const url = new URL(safeRedirect, window.location.origin)
    return {
      path: url.pathname,
      query: Object.fromEntries(url.searchParams),
      hash: url.hash,
    }
  } catch {
    return { path: safeRedirect }
  }
}
