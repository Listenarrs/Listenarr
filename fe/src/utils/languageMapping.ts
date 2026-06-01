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
// Language to region code mapping for Audible/Audible API
// Maps user-friendly language names to Audible market region codes
// Supported regions: us, ca, uk, au, fr, de, jp, it, in, es, br

export const languageToRegion: Record<string, string> = {
  english: 'us',
  'english-uk': 'uk',
  'english-ca': 'ca',
  'english-au': 'au',
  'english-in': 'in',
  german: 'de',
  french: 'fr',
  spanish: 'es',
  italian: 'it',
  portuguese: 'br',
  japanese: 'jp',
}

export const regionToLanguage: Record<string, string> = {
  us: 'english',
  uk: 'english-uk',
  gb: 'english-uk',
  ca: 'english-ca',
  au: 'english-au',
  in: 'english-in',
  de: 'german',
  fr: 'french',
  es: 'spanish',
  it: 'italian',
  br: 'portuguese',
  jp: 'japanese',
}

export const searchRegionOptions = [
  { value: 'us', label: 'United States (US)' },
  { value: 'uk', label: 'United Kingdom (UK)' },
  { value: 'ca', label: 'Canada (CA)' },
  { value: 'au', label: 'Australia (AU)' },
  { value: 'in', label: 'India (IN)' },
  { value: 'de', label: 'Germany (DE)' },
  { value: 'fr', label: 'France (FR)' },
  { value: 'es', label: 'Spain (ES)' },
  { value: 'it', label: 'Italy (IT)' },
  { value: 'br', label: 'Brazil (BR)' },
  { value: 'jp', label: 'Japan (JP)' },
] as const

export const preferredSearchLanguageOptions = [
  { value: 'all', label: 'All' },
  { value: 'arabic', label: 'Arabic' },
  { value: 'chinese', label: 'Chinese' },
  { value: 'czech', label: 'Czech' },
  { value: 'danish', label: 'Danish' },
  { value: 'dutch', label: 'Dutch' },
  { value: 'english', label: 'English' },
  { value: 'finnish', label: 'Finnish' },
  { value: 'french', label: 'French' },
  { value: 'german', label: 'German' },
  { value: 'greek', label: 'Greek' },
  { value: 'hebrew', label: 'Hebrew' },
  { value: 'hindi', label: 'Hindi' },
  { value: 'hungarian', label: 'Hungarian' },
  { value: 'italian', label: 'Italian' },
  { value: 'japanese', label: 'Japanese' },
  { value: 'korean', label: 'Korean' },
  { value: 'norwegian', label: 'Norwegian' },
  { value: 'polish', label: 'Polish' },
  { value: 'portuguese', label: 'Portuguese' },
  { value: 'russian', label: 'Russian' },
  { value: 'spanish', label: 'Spanish' },
  { value: 'swedish', label: 'Swedish' },
  { value: 'turkish', label: 'Turkish' },
] as const

const legacyRegionValueToRegion: Record<string, string> = {
  english: 'us',
  'english-uk': 'uk',
  'english-ca': 'ca',
  'english-au': 'au',
  'english-in': 'in',
  german: 'de',
  french: 'fr',
  spanish: 'es',
  italian: 'it',
  portuguese: 'br',
  japanese: 'jp',
  gb: 'uk',
}

const validSearchRegions = new Set<string>(searchRegionOptions.map((option) => option.value))
const validPreferredSearchLanguages = new Set<string>(
  preferredSearchLanguageOptions.map((option) => option.value),
)
const preferredSearchLanguageAliases: Record<string, string> = {
  all: 'all',
  any: 'all',
  arabic: 'arabic',
  ar: 'arabic',
  ara: 'arabic',
  chinese: 'chinese',
  zh: 'chinese',
  chi: 'chinese',
  zho: 'chinese',
  czech: 'czech',
  cs: 'czech',
  cze: 'czech',
  ces: 'czech',
  danish: 'danish',
  da: 'danish',
  dan: 'danish',
  dutch: 'dutch',
  nl: 'dutch',
  dut: 'dutch',
  nld: 'dutch',
  english: 'english',
  en: 'english',
  'en-us': 'english',
  'en-uk': 'english',
  'en-gb': 'english',
  'en-ca': 'english',
  'en-au': 'english',
  'en-in': 'english',
  'english-uk': 'english',
  'english-ca': 'english',
  'english-au': 'english',
  'english-in': 'english',
  finnish: 'finnish',
  fi: 'finnish',
  fin: 'finnish',
  french: 'french',
  fr: 'french',
  fre: 'french',
  fra: 'french',
  'fr-fr': 'french',
  german: 'german',
  de: 'german',
  ger: 'german',
  deu: 'german',
  deutsch: 'german',
  'de-de': 'german',
  greek: 'greek',
  el: 'greek',
  gre: 'greek',
  ell: 'greek',
  hebrew: 'hebrew',
  he: 'hebrew',
  heb: 'hebrew',
  iw: 'hebrew',
  hindi: 'hindi',
  hi: 'hindi',
  hin: 'hindi',
  hungarian: 'hungarian',
  hu: 'hungarian',
  hun: 'hungarian',
  magyar: 'hungarian',
  italian: 'italian',
  it: 'italian',
  ita: 'italian',
  'it-it': 'italian',
  japanese: 'japanese',
  ja: 'japanese',
  jpn: 'japanese',
  korean: 'korean',
  ko: 'korean',
  kor: 'korean',
  norwegian: 'norwegian',
  no: 'norwegian',
  nor: 'norwegian',
  nob: 'norwegian',
  nno: 'norwegian',
  polish: 'polish',
  pl: 'polish',
  pol: 'polish',
  'pl-pl': 'polish',
  portuguese: 'portuguese',
  pt: 'portuguese',
  por: 'portuguese',
  'pt-br': 'portuguese',
  'pt-pt': 'portuguese',
  russian: 'russian',
  ru: 'russian',
  rus: 'russian',
  'ru-ru': 'russian',
  spanish: 'spanish',
  es: 'spanish',
  spa: 'spanish',
  'es-es': 'spanish',
  swedish: 'swedish',
  sv: 'swedish',
  swe: 'swedish',
  'sv-se': 'swedish',
  turkish: 'turkish',
  tr: 'turkish',
  tur: 'turkish',
  'tr-tr': 'turkish',
}

function resolveSupportedPreferredSearchLanguage(
  value: string | undefined | null,
): string | undefined {
  const normalized = (value || '').trim().toLowerCase()
  if (!normalized) return undefined

  const alias = preferredSearchLanguageAliases[normalized]
  if (alias) return alias

  if (validPreferredSearchLanguages.has(normalized)) return normalized

  for (const option of preferredSearchLanguageOptions) {
    if (option.value !== 'all' && normalized.startsWith(option.value)) return option.value
  }

  return undefined
}

/**
 * Convert language name to region code
 * @param language - Language name (e.g., 'english', 'german')
 * @returns Region code (e.g., 'us', 'de') or 'us' as fallback
 */
export function getRegionFromLanguage(language: string): string {
  return languageToRegion[language.toLowerCase()] || 'us'
}

/**
 * Convert region code to language name
 * @param region - Region code (e.g., 'us', 'de')
 * @returns Language name (e.g., 'english', 'german') or 'english' as fallback
 */
export function getLanguageFromRegion(region: string): string {
  return regionToLanguage[region.toLowerCase()] || 'english'
}

export function getPrimaryPreferredSearchLanguageForRegion(
  region: string | undefined | null,
): string {
  return normalizePreferredSearchLanguage(getLanguageFromRegion(normalizeSearchRegion(region)))
}

export function normalizeSearchRegion(region: string | undefined | null): string {
  const normalized = (region || '').trim().toLowerCase()
  if (!normalized) return 'us'
  if (validSearchRegions.has(normalized)) return normalized
  return legacyRegionValueToRegion[normalized] || 'us'
}

export function normalizePreferredSearchLanguage(language: string | undefined | null): string {
  const normalized = (language || '').trim().toLowerCase()
  if (!normalized) return 'english'
  const directMatch = resolveSupportedPreferredSearchLanguage(normalized)
  if (directMatch) return directMatch
  if (validSearchRegions.has(normalized)) {
    return resolveSupportedPreferredSearchLanguage(getLanguageFromRegion(normalized)) || 'english'
  }
  const legacyRegion = legacyRegionValueToRegion[normalized]
  if (legacyRegion) {
    return resolveSupportedPreferredSearchLanguage(getLanguageFromRegion(legacyRegion)) || 'english'
  }
  if (normalized.startsWith('english')) return 'english'
  return 'english'
}

export function getPreferredSearchLanguageFilter(
  language: string | undefined | null,
): string | undefined {
  const normalized = normalizePreferredSearchLanguage(language)
  return normalized === 'all' ? undefined : normalized
}

export function normalizeSearchResultLanguage(
  language: string | undefined | null,
): string | undefined {
  const normalized = (language || '').trim().toLowerCase()
  if (!normalized) return undefined
  const directMatch = resolveSupportedPreferredSearchLanguage(normalized)
  if (directMatch && directMatch !== 'all') return directMatch
  if (validSearchRegions.has(normalized)) {
    const mapped = resolveSupportedPreferredSearchLanguage(getLanguageFromRegion(normalized))
    return mapped === 'all' ? undefined : mapped
  }
  const legacyRegion = legacyRegionValueToRegion[normalized]
  if (legacyRegion) {
    const mapped = resolveSupportedPreferredSearchLanguage(getLanguageFromRegion(legacyRegion))
    return mapped === 'all' ? undefined : mapped
  }
  return undefined
}
