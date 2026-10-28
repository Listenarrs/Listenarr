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
/**
 * Small path utility helpers used across UI components.
 * Keep these functions small and dependency-free so they are easy to reason about
 * and simple to unit test if needed.
 */

export type PathKind = 'windows' | 'unix' | 'unknown'
export type PathCaseSensitivity = 'Unknown' | 'Sensitive' | 'Insensitive'

export interface DestinationPathValidationOptions {
  pathKind?: PathKind
  caseSensitivity?: PathCaseSensitivity
  sourcePath?: string | null
  requireAbsolute?: boolean
  allowFileSystemRoot?: boolean
}

const WINDOWS_RESERVED_DEVICE_PATTERN =
  /^(con|prn|aux|nul|com(?:[1-9]|[¹²³])|lpt(?:[1-9]|[¹²³]))$/iu

export function toForward(s: string | null | undefined): string {
  return (s || '').replace(/\\/g, '/')
}

export function trimTrailingSlash(s: string): string {
  if (s === '/') return s
  const driveRoot = s.match(/^([a-zA-Z]:)([\\/]+)$/)
  if (driveRoot) return `${driveRoot[1]}${driveRoot[2][0]}`
  let out = s
  while (out.endsWith('/') || out.endsWith('\\')) out = out.slice(0, -1)
  return out
}

export function trimTrailingDirectorySeparators(s: string, pathKind: PathKind): string {
  if (s === '/') return s
  if (pathKind === 'windows') return trimTrailingSlash(s)

  let out = s
  while (out.endsWith('/')) out = out.slice(0, -1)
  return out
}

export function detectPathKind(
  s: string | null | undefined,
  expectedKind: PathKind = 'unknown',
): PathKind {
  const value = s || ''
  const isForwardSlashUnc = /^\/\/[^\\/]+[\\/][^\\/]+/.test(value)

  if (expectedKind === 'windows' && isForwardSlashUnc) return 'windows'
  if (expectedKind === 'unix' && value.startsWith('/')) return 'unix'
  if (/^[a-zA-Z]:/.test(value)) return 'windows'
  if (/^\\\\[^\\/]+[\\/][^\\/]+/.test(value)) return 'windows'
  if (isForwardSlashUnc) return 'unknown'
  if (value.startsWith('/')) return 'unix'
  if (value.includes('\\')) return 'windows'
  return 'unknown'
}

export function splitPathSegments(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): string[] {
  const value = s || ''
  const kind = pathKind === 'unknown' ? detectPathKind(value) : pathKind
  if (kind === 'windows') return value.replace(/\\/g, '/').split('/')
  return value.split('/')
}

export function normalizeForCompare(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
  caseSensitivity: PathCaseSensitivity = 'Unknown',
): string {
  const rawValue = s || ''
  const kind = pathKind === 'unknown' ? detectPathKind(rawValue) : pathKind
  const value = trimTrailingDirectorySeparators(rawValue, kind)
  const normalized = kind === 'windows' ? value.replace(/\\/g, '/') : value
  const canonicalSyntax =
    kind === 'windows'
      ? normalized.replace(/^([a-z]):/i, (_, drive) => `${drive.toUpperCase()}:`)
      : normalized
  const caseInsensitive =
    caseSensitivity === 'Insensitive' || (caseSensitivity === 'Unknown' && kind === 'windows')
  return caseInsensitive ? canonicalSyntax.toLowerCase() : canonicalSyntax
}

export function pathsEqual(
  first: string | null | undefined,
  second: string | null | undefined,
  pathKind: PathKind = 'unknown',
  caseSensitivity: PathCaseSensitivity = 'Unknown',
): boolean {
  if (!first || !second) return false

  const kind = pathKind === 'unknown' ? detectPathKind(first) : pathKind
  return (
    normalizeForCompare(first, kind, caseSensitivity) ===
    normalizeForCompare(second, kind, caseSensitivity)
  )
}

export function pathIsInside(
  candidate: string | null | undefined,
  root: string | null | undefined,
  pathKind: PathKind = 'unknown',
  caseSensitivity: PathCaseSensitivity = 'Unknown',
): boolean {
  if (!candidate || !root) return false

  const kind = pathKind === 'unknown' ? detectPathKind(candidate) : pathKind
  const normalizedCandidate = normalizeForCompare(candidate, kind, caseSensitivity)
  const normalizedRoot = normalizeForCompare(root, kind, caseSensitivity)
  if (!normalizedCandidate || !normalizedRoot || normalizedCandidate === normalizedRoot)
    return false

  const rootWithSeparator = normalizedRoot.endsWith('/') ? normalizedRoot : `${normalizedRoot}/`
  return normalizedCandidate.startsWith(rootWithSeparator)
}

export function pathsOverlap(
  first: string | null | undefined,
  second: string | null | undefined,
  pathKind: PathKind = 'unknown',
  caseSensitivity: PathCaseSensitivity = 'Unknown',
): boolean {
  return (
    pathsEqual(first, second, pathKind, caseSensitivity) ||
    pathIsInside(first, second, pathKind, caseSensitivity) ||
    pathIsInside(second, first, pathKind, caseSensitivity)
  )
}

export function isAbsolutePath(s: string, pathKind: PathKind = 'unknown'): boolean {
  const kind = pathKind === 'unknown' ? detectPathKind(s) : pathKind
  if (kind === 'unix') return s.startsWith('/')
  if (kind === 'windows') {
    return /^[a-zA-Z]:[\\/]/.test(s) || /^[\\/]{2}/.test(s) || /^[\\/]$/.test(s)
  }
  return /^([a-zA-Z]:[\\/]|[\\/])/.test(s)
}

/**
 * Returns true when an input begins from a filesystem root rather than being
 * relative to a selected authority. Windows root-relative values such as
 * `\\Author\\Title` count as rooted even though they do not include a drive.
 */
export function isRootedPath(s: string, pathKind: PathKind = 'unknown'): boolean {
  const kind = pathKind === 'unknown' ? detectPathKind(s) : pathKind
  if (kind === 'unix') return s.startsWith('/')
  if (kind === 'windows') return /^[a-zA-Z]:[\\/]/.test(s) || /^[\\/]/.test(s)
  return /^([a-zA-Z]:[\\/]|[\\/])/.test(s)
}

export function isFileSystemRoot(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  const value = s || ''
  const kind = pathKind === 'unknown' ? detectPathKind(value) : pathKind
  if (kind === 'unix') return /^\/+$/u.test(value)
  if (kind !== 'windows') return false

  const normalized = value.replace(/\\/g, '/')
  if (normalized === '/') return true
  if (/^[a-zA-Z]:\/+$/u.test(normalized)) return true
  if (!normalized.startsWith('//')) return false

  const segments = normalized
    .slice(2)
    .split('/')
    .filter((segment) => segment.length > 0)
  return segments.length === 2
}

export function hasRelativePathSegment(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  return splitPathSegments(s, pathKind).some((segment) => segment === '.' || segment === '..')
}

export function hasParentTraversalSegment(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  return splitPathSegments(s, pathKind).some((segment) => segment === '..')
}

export function hasEmptyMiddlePathSegment(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  const value = s || ''
  const kind = pathKind === 'unknown' ? detectPathKind(value) : pathKind
  const normalized = trimTrailingSlash(kind === 'windows' ? value.replace(/\\/g, '/') : value)
  if (!normalized || normalized === '/' || /^[a-zA-Z]:\/$/.test(normalized)) return false

  if (kind === 'windows' && normalized.startsWith('//')) {
    return normalized
      .slice(2)
      .split('/')
      .some((segment) => segment === '')
  }

  const pathWithoutRoot =
    kind === 'unix' || (kind === 'unknown' && normalized.startsWith('//'))
      ? normalized.replace(/^\/+/, '')
      : normalized
  return pathWithoutRoot.split('/').some((segment) => segment === '')
}

export function hasControlCharacter(s: string | null | undefined): boolean {
  return /[\u0000-\u001f\u007f]/.test(s || '')
}

export function hasOuterWhitespace(s: string | null | undefined): boolean {
  const value = s || ''
  return value.length > 0 && value !== value.trim()
}

export function hasPathSegmentOuterWhitespace(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  return splitPathSegments(s, pathKind)
    .filter((segment) => segment.length > 0)
    .some((segment) => segment !== segment.trim())
}

export function hasWindowsDriveRelativePath(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  const value = s || ''
  const kind = pathKind === 'unknown' ? detectPathKind(value) : pathKind
  return kind === 'windows' && /^[a-zA-Z]:(?![\\/])/.test(value)
}

export function hasIncompleteWindowsUncAuthority(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  const value = s || ''
  const kind = pathKind === 'unknown' ? detectPathKind(value) : pathKind
  if (kind !== 'windows') return false

  const normalized = value.replace(/\\/g, '/')
  if (!normalized.startsWith('//')) return false

  const authoritySegments = trimTrailingSlash(normalized).slice(2).split('/')
  return authoritySegments.length < 2 || !authoritySegments[0] || !authoritySegments[1]
}

export function hasWindowsTrailingSpaceOrPeriodSegment(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  const value = s || ''
  const kind = pathKind === 'unknown' ? detectPathKind(value) : pathKind
  if (kind !== 'windows') return false

  return splitPathSegments(trimTrailingSlash(value), 'windows')
    .filter((segment) => segment.length > 0)
    .some((segment) => /[ .]$/.test(segment))
}

export function hasWindowsInvalidCharacter(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  const value = s || ''
  const kind = pathKind === 'unknown' ? detectPathKind(value) : pathKind
  if (kind !== 'windows') return false
  if (/[<>"|?*]/.test(value)) return true

  const withoutDriveColon = /^[a-zA-Z]:/.test(value) ? value.slice(2) : value
  return withoutDriveColon.includes(':')
}

export function hasWindowsReservedDeviceSegment(
  s: string | null | undefined,
  pathKind: PathKind = 'unknown',
): boolean {
  const value = s || ''
  const kind = pathKind === 'unknown' ? detectPathKind(value) : pathKind
  if (kind !== 'windows') return false

  const normalized = trimTrailingSlash(value.replace(/\\/g, '/'))
  let segments = normalized.split('/').filter((segment) => segment.length > 0)

  if (/^[a-zA-Z]:$/.test(segments[0] || '')) {
    segments = segments.slice(1)
  }

  return segments.some((segment) => {
    const baseName = segment.trimEnd().split('.')[0]
    return WINDOWS_RESERVED_DEVICE_PATTERN.test(baseName)
  })
}

export function validateLibraryDestinationPath(
  s: string | null | undefined,
  options: DestinationPathValidationOptions = {},
): string | null {
  if (!s) return null

  const pathKind =
    options.pathKind === 'unknown' || !options.pathKind ? detectPathKind(s) : options.pathKind

  if (hasControlCharacter(s)) {
    return 'Destination folder cannot contain control characters.'
  }

  if (hasWindowsDriveRelativePath(s, pathKind)) {
    return 'Windows destination folders must include a separator after the drive letter, such as C:\\.'
  }

  if (options.requireAbsolute && !isAbsolutePath(s, pathKind)) {
    return 'Destination folder must be an absolute directory path.'
  }

  if (options.allowFileSystemRoot === false && isFileSystemRoot(s, pathKind)) {
    return 'Destination folder cannot be the filesystem root.'
  }

  if (hasParentTraversalSegment(s, pathKind)) {
    return 'Path traversal is not allowed in the destination folder. Remove parent directory segments and choose the actual target folder instead.'
  }

  if (hasRelativePathSegment(s, pathKind)) {
    return 'Destination folder cannot contain current-directory path segments. Choose the actual target folder instead.'
  }

  if (hasEmptyMiddlePathSegment(s, pathKind)) {
    return 'Destination folder cannot contain empty path segments. Remove repeated path separators.'
  }

  if (hasIncompleteWindowsUncAuthority(s, pathKind)) {
    return 'Windows UNC destination folders must include both a server and share.'
  }

  if (hasWindowsTrailingSpaceOrPeriodSegment(s, pathKind)) {
    return 'Windows destination folder segments cannot end with a space or period.'
  }

  if (hasWindowsInvalidCharacter(s, pathKind)) {
    return 'Destination folder contains characters that are invalid on Windows.'
  }

  if (hasWindowsReservedDeviceSegment(s, pathKind)) {
    return 'Destination folder contains a reserved Windows device name.'
  }

  if (
    options.sourcePath &&
    pathsEqual(s, options.sourcePath, pathKind, options.caseSensitivity ?? 'Unknown')
  ) {
    return 'Destination folder must be different from the current source folder.'
  }

  return null
}

/**
 * Remove the complete configured root from an equal or contained absolute path.
 * Returns null when the value is outside the root or uses incompatible syntax.
 */
export function stripRootPrefix(
  root: string,
  value: string,
  caseSensitivity: PathCaseSensitivity = 'Unknown',
  pathKind: PathKind = 'unknown',
): string | null {
  if (!root || !value) return null
  try {
    const rootKind = pathKind === 'unknown' ? detectPathKind(root) : pathKind
    const valueKind = detectPathKind(value, rootKind)
    if (rootKind === 'unknown' || (valueKind !== 'unknown' && valueKind !== rootKind)) return null

    const normalizedRoot = trimTrailingDirectorySeparators(
      rootKind === 'windows' ? toForward(root) : root,
      rootKind,
    )
    const normalizedValue = trimTrailingDirectorySeparators(
      rootKind === 'windows' ? toForward(value) : value,
      rootKind,
    )
    const comparableRoot = normalizeForCompare(normalizedRoot, rootKind, caseSensitivity)
    const comparableValue = normalizeForCompare(normalizedValue, rootKind, caseSensitivity)
    const useBackslash = rootKind === 'windows' && root.includes('\\')

    if (comparableValue === comparableRoot) return ''

    const rootBoundary = comparableRoot.endsWith('/') ? comparableRoot : `${comparableRoot}/`
    if (!comparableValue.startsWith(rootBoundary)) return null

    const rel = normalizedValue.slice(normalizedRoot.length).replace(/^\/+/, '')
    return useBackslash ? rel.replace(/\//g, '\\') : rel
  } catch {
    return null
  }
}

export function joinPaths(
  root: string | null | undefined,
  relative: string | null | undefined,
  pathKind: PathKind = 'unknown',
): string {
  if (!root) return relative || ''
  const rootKind = pathKind === 'unknown' ? detectPathKind(root) : pathKind
  const useBackslash = rootKind === 'windows' && root.includes('\\')
  const normalizedRoot = rootKind === 'windows' ? root.replace(/\\/g, '/') : root
  const r = trimTrailingDirectorySeparators(normalizedRoot, rootKind)
  const rel = (relative || '').toString().replace(/^\/+/, '')
  const combined = rel ? `${r}${r.endsWith('/') ? '' : '/'}${rel}` : r
  return useBackslash ? combined.replace(/\//g, '\\') : combined
}
