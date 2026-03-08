/**
 * Utility functions for formatting search result metadata
 * Shared across AddNewView, AudiobookDetailsModal, and other components
 */

/**
 * Format an ISO 8601 date string to human-readable format
 * @param dateString - ISO 8601 date string (e.g., "2015-10-05")
 * @returns Formatted date string (e.g., "Oct 05, 2015")
 * @example
 * formatDate("2015-10-05") // "Oct 05, 2015"
 * formatDate("invalid") // "invalid"
 */
export const formatDate = (dateString: string): string => {
  if (!dateString) return ''

  try {
    // For ISO date strings, we need to handle timezone offset issues
    // Parse manually to ensure we use the date as-is without timezone conversion
    const parts = (dateString.split('T')[0] || dateString).split('-')
    if (parts.length !== 3) {
      // Try parsing as-is if not ISO format
      const date = new Date(dateString)
      if (isNaN(date.getTime())) return dateString
      return date.toLocaleDateString('en-US', {
        year: 'numeric',
        month: 'short',
        day: '2-digit', // pad day with leading zero to match tests (e.g. 'Oct 05, 2015')
      })
    }

    // Validate numeric ranges for month/day to avoid JS "rollover" behavior
    const year = parseInt(parts[0]!, 10)
    const monthNum = parseInt(parts[1]!, 10)
    const dayNum = parseInt(parts[2]!, 10)

    // Reject obviously invalid dates (e.g. month 13)
    if (isNaN(year) || isNaN(monthNum) || isNaN(dayNum)) return dateString
    if (monthNum < 1 || monthNum > 12 || dayNum < 1 || dayNum > 31) return dateString

    const month = monthNum - 1 // JS months are 0-indexed
    const day = dayNum

    const date = new Date(year, month, day)
    if (isNaN(date.getTime())) return dateString

    return date.toLocaleDateString('en-US', {
      year: 'numeric',
      month: 'short',
      day: '2-digit',
    })
  } catch {
    return dateString
  }
}

/**
 * Format minutes into human-readable runtime
 * @param minutes - Duration in minutes
 * @returns Formatted runtime string (e.g., "12h 45m", "2h", "45m")
 * @example
 * formatRuntime(765) // "12h 45m"
 * formatRuntime(120) // "2h"
 * formatRuntime(45) // "45m"
 */
export const formatRuntime = (raw: number): string => {
  if (!raw || raw <= 0) return 'Unknown'

  // Guard against legacy data stored in seconds (> 333 hours is unrealistic for minutes).
  // Values >= 20000 are treated as seconds and converted to minutes.
  const normalized = raw >= 20000 ? Math.round(raw / 60) : raw
  const totalMinutes = Math.floor(normalized)
  const hours = Math.floor(totalMinutes / 60)
  const mins = totalMinutes % 60

  if (hours > 0 && mins > 0) return `${hours}h ${mins}m`
  if (hours > 0) return `${hours}h`
  return `${mins}m`
}

/**
 * Capitalize the first letter of a language code
 * @param language - Language string (e.g., "english", "spanish")
 * @returns Capitalized language (e.g., "English", "Spanish")
 * @example
 * capitalizeLanguage("english") // "English"
 * capitalizeLanguage("german") // "German"
 */
export const capitalizeLanguage = (language: string | undefined): string => {
  if (!language) return ''

  // Handle language codes like "english-uk" -> "English (UK)"
  if (language.includes('-')) {
    const parts = language.split('-')
    const lang = parts[0]!
    const region = parts[1]!
    const capitalizedLang = lang.charAt(0).toUpperCase() + lang.slice(1).toLowerCase()
    const upperRegion = region.toUpperCase()
    return `${capitalizedLang} (${upperRegion})`
  }

  return language.charAt(0).toUpperCase() + language.slice(1).toLowerCase()
}

/**
 * Capitalize the first letter of any string
 * @param text - Input string
 * @returns String with first letter capitalized
 * @example
 * capitalizeFirst("hello world") // "Hello world"
 */
export const capitalizeFirst = (text: string): string => {
  if (!text) return ''
  return text.charAt(0).toUpperCase() + text.slice(1)
}

/**
 * Extract year from date string
 * @param dateString - Date string (e.g., "2015-10-05" or "2015")
 * @returns Year as number or undefined
 * @example
 * getYearFromDate("2015-10-05") // 2015
 * getYearFromDate("2015") // 2015
 */
export const getYearFromDate = (dateString: string | undefined): number | undefined => {
  if (!dateString) return undefined

  try {
    const year = parseInt(dateString.substring(0, 4), 10)
    return isNaN(year) ? undefined : year
  } catch {
    return undefined
  }
}
