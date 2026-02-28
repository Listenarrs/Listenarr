/**
 * Helper functions for normalizing and processing search result data
 * Shared across AddNewView and other search interfaces
 */

import { getYearFromDate } from './searchResultFormatting'

/**
 * Generic search result or metadata object with flexible properties
 */
export interface NormalizedResult extends Record<string, unknown> {
  asin?: string
  id?: string
  title?: string
  author?: string | { name?: string; Name?: string }
  authors?: Array<string | { name?: string; Name?: string }>
  publisher?: string | string[]
  narrators?: Array<string | { name?: string; Name?: string }>
  narrator?: string | Array<string | { name?: string; Name?: string }>
  publishedDate?: string | Date
  releaseDate?: string | Date
  runtimeLengthMin?: number
  runtime?: number
  description?: string
  source?: string
  metadataSource?: string
  imageUrl?: string
  series?: unknown
}

/**
 * Utility for safely picking a value from an object by multiple key names
 * Returns the first non-null, non-undefined value found
 * @internal
 */
function pick<T>(obj: Record<string, unknown>, ...keys: string[]): T | undefined {
  for (const k of keys) {
    const v = obj[k]
    if (v !== undefined && v !== null) return v as T
  }
  return undefined
}

/**
 * Extract author names from various result formats
 * @param result - Search result or metadata object
 * @returns Array of author names (empty if none found)
 * @example
 * extractAuthors({ author: "Frank Herbert" }) // ["Frank Herbert"]
 * extractAuthors({ authors: [{ name: "Frank Herbert" }] }) // ["Frank Herbert"]
 */
export const extractAuthors = (result: NormalizedResult): string[] => {
  // Direct author string
  const authorVal = pick<string>(result, 'author', 'Artist', 'artist', 'Author')
  if (authorVal && typeof authorVal === 'string' && authorVal.trim().length) {
    return [authorVal.trim()]
  }

  // Authors array (from Audimeta or other sources)
  const authorsArray = (result.authors ?? result.Authors) as
    | Array<string | { name?: string; Name?: string }>
    | undefined

  if (Array.isArray(authorsArray) && authorsArray.length) {
    return authorsArray
      .map((a: unknown) => {
        if (typeof a === 'string') return a.trim()
        if (typeof a === 'object' && a) {
          const rec = a as Record<string, unknown>
          return ((rec.name as string) || (rec.Name as string) || '').trim()
        }
        return String(a).trim()
      })
      .filter((n) => !!n)
  }

  return []
}

/**
 * Extract published date from various date fields
 * Handles both ISO strings and Date objects
 * @param result - Search result or metadata object
 * @returns Full date string (ISO format) or undefined
 * @example
 * extractPublishedDate({ publishedDate: "2015-10-05" }) // "2015-10-05"
 * extractPublishedDate({ releaseDate: new Date("2015-10-05") }) // "2015-10-05"
 */
export const extractPublishedDate = (result: NormalizedResult): string | undefined => {
  const dateVal = pick<string | Date>(
    result,
    'publishedDate',
    'releaseDate',
    'ReleaseDate',
    'release_date',
    'Release_date',
  )

  if (!dateVal) return undefined

  if (typeof dateVal === 'object' && typeof (dateVal as Date).toISOString === 'function') {
    return (dateVal as Date).toISOString().split('T')[0]
  }

  if (typeof dateVal === 'string') {
    return dateVal
  }

  return undefined
}

/**
 * Extract narrator names from various formats
 * @param result - Search result or metadata object
 * @returns Comma-separated narrator names (empty string if none)
 * @example
 * extractNarrators({ narrators: [{ name: "Scott Brick" }] }) // "Scott Brick"
 * extractNarrators({ narrator: "Scott Brick" }) // "Scott Brick"
 */
export const extractNarrators = (result: NormalizedResult): string => {
  const narr = pick<unknown>(
    result,
    'narrators',
    'Narrators',
    'narrator',
    'Narrator',
  )

  if (!narr) return ''

  if (Array.isArray(narr)) {
    return (narr as unknown[])
      .map((n: unknown) => {
        if (typeof n === 'string') return n.trim()
        if (typeof n === 'object' && n) {
          const rec = n as Record<string, unknown>
          return ((rec.name as string) || (rec.Name as string) || '').trim()
        }
        return String(n).trim()
      })
      .filter(Boolean)
      .join(', ')
  }

  if (typeof narr === 'string') {
    return narr.trim()
  }

  return ''
}

/**
 * Normalize runtime to minutes
 * Handles both seconds and minutes input
 * @param raw - Runtime value (may be in seconds or minutes)
 * @returns Runtime in minutes, or undefined if invalid
 * @example
 * normalizeRuntime(3600) // 60 (1 hour)
 * normalizeRuntime(60) // 60 (already in minutes)
 * normalizeRuntime('invalid') // undefined
 */
export const normalizeRuntime = (raw: unknown): number | undefined => {
  if (raw === undefined || raw === null) return undefined

  const num = Number(raw)
  if (isNaN(num) || num <= 0) return undefined

  // Convert from seconds to minutes when the value looks like seconds (heuristic > 1000).
  // Treat values <= 1000 as minutes already. Round to the nearest minute for safety.
  return num > 1000 ? Math.round(num / 60) : Math.round(num)
}

/**
 * Process series array into formatted display strings
 * Handles nested series objects with positions
 * @param raw - Series data (array or single object)
 * @returns Object with list (formatted strings) and display (first item)
 * @example
 * processSeries([{ name: "Dune", position: "1" }])
 * // { list: ["Dune #1"], display: "Dune #1" }
 */
export const processSeries = (raw: unknown): { list: string[]; display: string } => {
  const list: string[] = []

  if (Array.isArray(raw) && raw.length) {
    list.push(
      ...raw
        .map((s: unknown) => {
          if (typeof s === 'object' && s) {
            const rec = s as Record<string, unknown>
            const name = ((rec.name as string) || (rec.Name as string) || String(s)).trim()
            const position = rec.position as string | undefined
            return position ? `${name} #${position}` : name
          }
          return String(s).trim()
        })
        .filter(Boolean),
    )
  }

  return {
    list,
    display: list[0] ?? '',
  }
}

/**
 * Normalize source label to user-friendly format
 * Converts technical source names to display names
 * @param source - Source identifier
 * @returns Display-friendly source name
 * @example
 * normalizeSource("audimeta") // "Audible"
 * normalizeSource("openlibrary") // "OpenLibrary"
 */
export const normalizeSource = (source: string | undefined): string => {
  if (!source) return ''

  const lower = source.toLowerCase()
  if (lower.includes('audimeta')) return 'Audible'
  if (lower.includes('openlibrary')) return 'OpenLibrary'
  if (lower.includes('audible')) return 'Audible'

  return source
}

/**
 * Check if result looks like Audimeta-enriched data
 * Based on metadata source, enrichment flags, or presence of ASIN
 * @param result - Search result or metadata object
 * @returns True if result appears to be from Audimeta
 */
export const isAudimetaSource = (result: NormalizedResult): boolean => {
  return (
    (result.metadataSource && String(result.metadataSource).toLowerCase() === 'audimeta') ||
    Boolean((result as Record<string, unknown>)['isEnriched']) ||
    Boolean(result.asin)
  )
}

/**
 * Get primary identifier from result (ASIN preferred, falls back to ID or title)
 * @param result - Search result or metadata object
 * @returns Primary identifier string
 */
export const getPrimaryId = (result: NormalizedResult): string => {
  return String(result.asin || result.id || result.title || '')
}

/**
 * Extract subtitle from result
 * Handles multiple property name variations
 * @param result - Search result or metadata object
 * @returns Subtitle string or undefined
 */
export const extractSubtitle = (result: NormalizedResult): string | undefined => {
  const sub = pick<unknown>(
    result,
    'subtitle',
    'Subtitle',
    'subtitles',
    'Subtitles',
  )

  if (!sub) return undefined

  if (Array.isArray(sub)) {
    return (sub as unknown[]).map(String).join(', ')
  }

  if (typeof sub === 'string') return sub

  return String(sub)
}

/**
 * Extract description from result
 * Handles multiple property name variations
 * @param result - Search result or metadata object
 * @returns Description string or undefined
 */
export const extractDescription = (result: NormalizedResult): string | undefined => {
  return pick<string>(result, 'description', 'Description')
}

/**
 * Extract publisher(s) from result
 * Always returns array for consistency
 * @param result - Search result or metadata object
 * @returns Array of publisher names
 */
export const extractPublishers = (result: NormalizedResult): string[] => {
  const pub = pick<unknown>(result, 'publisher', 'Publisher')

  if (!pub) return []

  if (Array.isArray(pub)) {
    return pub
      .map((p) => (typeof p === 'string' ? p.trim() : String(p).trim()))
      .filter(Boolean)
  }

  if (typeof pub === 'string') {
    return pub.trim() ? [pub.trim()] : []
  }

  return []
}

/**
 * Get language/locale from result
 * @param result - Search result or metadata object
 * @returns Language code (e.g., "english", "german")
 */
export const extractLanguage = (result: NormalizedResult): string | undefined => {
  return pick<string>(result, 'language', 'Language', 'locale', 'Locale')
}

/**
 * Normalize all metadata from a search result in one call
 * Useful for transforming raw API results
 * @param result - Raw search result
 * @returns Normalized metadata object
 */
export const normalizeResultMetadata = (
  result: NormalizedResult,
): {
  authors: string[]
  narrators: string
  subtitle?: string
  description?: string
  publishedDate?: string
  publishYear?: number
  runtime?: number
  series: { list: string[]; display: string }
  publishers: string[]
  language?: string
  primaryId: string
  isAudimeta: boolean
} => ({
  authors: extractAuthors(result),
  narrators: extractNarrators(result),
  subtitle: extractSubtitle(result),
  description: extractDescription(result),
  publishedDate: extractPublishedDate(result),
  publishYear: getYearFromDate(extractPublishedDate(result)),
  runtime: normalizeRuntime(result.runtimeLengthMin ?? result.runtime),
  series: processSeries(result.series),
  publishers: extractPublishers(result),
  language: extractLanguage(result),
  primaryId: getPrimaryId(result),
  isAudimeta: isAudimetaSource(result),
})

/**
 * Safely return an optional string value from loosely-shaped API responses.
 * If the value is an array, returns the first non-empty string. If it's an
 * object, attempts to coerce a meaningful string. Otherwise returns undefined.
 */
export const getOptionalString = (val: unknown): string | undefined => {
  if (val === undefined || val === null) return undefined
  if (Array.isArray(val)) {
    for (const v of val) {
      if (typeof v === 'string' && v.trim()) return v.trim()
    }
    return undefined
  }
  if (typeof val === 'string') return val.trim() || undefined
  if (typeof val === 'object') {
    try {
      const obj = val as Record<string, unknown>
      // Common patterns: { value: '...' } or { text: '...' }
      if (typeof obj['value'] === 'string' && obj['value'].trim()) return obj['value'].trim()
      if (typeof obj['text'] === 'string' && obj['text'].trim()) return obj['text'].trim()
      // Fallback to JSON string
      const s = JSON.stringify(obj)
      return s === '{}' ? undefined : s
    } catch {
      return undefined
    }
  }
  return String(val)
}

/**
 * Heuristic check whether an OpenLibrary-style book object is addable via
 * the Add New flow. Requires a title and at least one ISBN (string) present.
 */
export const canAddOpenLibraryResult = (book: unknown): boolean => {
  if (!book || typeof book !== 'object') return false
  const b = book as Record<string, unknown>
  const title = (b['title'] ?? b['Title']) as unknown
  if (!title || (typeof title === 'string' && !title.trim())) return false
  const isbn = b['isbn'] ?? b['ISBN']
  if (!isbn) return false
  if (Array.isArray(isbn) && isbn.length > 0 && typeof isbn[0] === 'string' && isbn[0].trim()) return true
  if (typeof isbn === 'string' && isbn.trim()) return true
  return false
}
