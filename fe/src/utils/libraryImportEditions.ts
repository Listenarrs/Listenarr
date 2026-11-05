import type { SearchResult } from '@/types'

// A recording whose runtime is within this share of the file's length is treated as the same edition.
export const EDITION_RUNTIME_TOLERANCE = 0.03

/** Minutes between a result's runtime and the file's length; undefined when either is unknown. */
export function runtimeDeltaMinutes(
  result: Pick<SearchResult, 'lengthMinutes'>,
  durationSeconds?: number,
): number | undefined {
  if (!result.lengthMinutes || !durationSeconds) return undefined
  return Math.round(result.lengthMinutes - durationSeconds / 60)
}

/** True when the result's runtime is close enough to the file's length to be the same recording. */
export function runtimeMatches(
  result: Pick<SearchResult, 'lengthMinutes'>,
  durationSeconds?: number,
): boolean {
  if (!result.lengthMinutes || !durationSeconds) return false
  const minutes = durationSeconds / 60
  return Math.abs(result.lengthMinutes - minutes) / minutes <= EDITION_RUNTIME_TOLERANCE
}

/**
 * Audible results first, then editions from other catalogues that Audible's search did not return
 * (same ASIN = same recording, so the Audible entry wins).
 */
export function mergeEditionResults(
  audible: SearchResult[],
  editions: SearchResult[],
): SearchResult[] {
  const seen = new Set(audible.map((r) => r.asin?.toUpperCase()).filter(Boolean))
  const extra = editions.filter((e) => {
    const asin = e.asin?.toUpperCase()
    if (!asin || seen.has(asin)) return false
    seen.add(asin)
    return true
  })
  return [...audible, ...extra]
}

/**
 * When the file's length is known, the result whose runtime is closest to it (within tolerance);
 * otherwise undefined so the caller keeps its title/author-based choice.
 */
export function pickByRuntime(
  results: SearchResult[],
  durationSeconds?: number,
): SearchResult | undefined {
  if (!durationSeconds) return undefined
  const minutes = durationSeconds / 60
  return results
    .filter((r) => runtimeMatches(r, durationSeconds))
    .sort(
      (a, b) =>
        Math.abs((a.lengthMinutes ?? 0) - minutes) - Math.abs((b.lengthMinutes ?? 0) - minutes),
    )[0]
}

export function formatRuntime(minutes?: number): string {
  if (!minutes) return ''
  const h = Math.floor(minutes / 60)
  const m = Math.round(minutes % 60)
  return h > 0 ? `${h}h ${m}m` : `${m}m`
}
