/**
 * Small path utility helpers used across UI components.
 * Keep these functions small and dependency-free so they are easy to reason about
 * and simple to unit test if needed.
 */

export function toForward(s: string | null | undefined): string {
  return (s || '').replace(/\\/g, '/')
}

export function trimTrailingSlash(s: string): string {
  let out = s
  while (out.endsWith('/') || out.endsWith('\\')) out = out.slice(0, -1)
  return out
}

export function normalizeForCompare(s: string | null | undefined): string {
  return toForward(trimTrailingSlash(s || '')).toLowerCase()
}

export function isAbsolutePath(s: string): boolean {
  return /^([a-zA-Z]:[\\/]|[\\/])/.test(s)
}

/**
 * If `value` contains the configured `root` prefix, remove it and return the
 * relative portion (respecting backslash style). Returns null if no match.
 */
export function stripRootPrefix(root: string, value: string): string | null {
  if (!root || !value) return null
  try {
    const nroot = toForward(root).toLowerCase()
    const nval = toForward(value).toLowerCase()

    if (nval.includes(nroot)) {
      const idx = nval.indexOf(nroot)
      const rel = toForward(value).slice(idx + nroot.length).replace(/^\/+/, '')
      const useBackslash = root.includes('\\')
      return useBackslash ? rel.replace(/\//g, '\\') : rel
    }

    // fallback: try matching two-segment windows from the end toward the start
    const segs = nroot.split('/')
    for (let i = Math.max(0, segs.length - 2); i >= 0; i--) {
      const two = segs.slice(i, i + 2).join('/')
      if (two && nval.includes(two)) {
        const idx = nval.indexOf(two)
        const rel = toForward(value).slice(idx + two.length).replace(/^\/+/, '')
        const useBackslash = root.includes('\\')
        return useBackslash ? rel.replace(/\//g, '\\\\') : rel
      }
    }
  } catch (err) {
    // noop — fall through to null
  }

  return null
}

export function joinPaths(root: string | null | undefined, relative: string | null | undefined): string {
  if (!root) return relative || ''
  const useBackslash = (root || '').includes('\\')
  const r = trimTrailingSlash(toForward(root || ''))
  const rel = (relative || '').toString().replace(/^\/+/, '')
  const combined = rel ? `${r}/${rel}` : r
  return useBackslash ? combined.replace(/\//g, '\\') : combined
}

