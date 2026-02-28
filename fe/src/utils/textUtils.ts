const NAMED_HTML_ENTITIES: Record<string, string> = {
  amp: '&',
  lt: '<',
  gt: '>',
  quot: '"',
  apos: "'",
  nbsp: ' ',
  ndash: '-',
  mdash: '-',
  lsquo: "'",
  rsquo: "'",
  ldquo: '"',
  rdquo: '"',
  hellip: '...',
}

function decodeNumericEntity(entityBody: string): string | null {
  const isHex = entityBody.startsWith('#x') || entityBody.startsWith('#X')
  const digits = entityBody.slice(isHex ? 2 : 1)
  if (!digits) return null

  const codePoint = Number.parseInt(digits, isHex ? 16 : 10)
  if (!Number.isFinite(codePoint) || codePoint < 0 || codePoint > 0x10ffff) {
    return null
  }

  try {
    return String.fromCodePoint(codePoint)
  } catch {
    return null
  }
}

/**
 * Decodes a small, common subset of HTML entities without using `innerHTML`.
 * This avoids DOM-based HTML re-interpretation and satisfies CodeQL's DOM XSS rule.
 */
export function decodeHtmlEntities(text: string): string {
  if (!text) return text

  return text.replace(/&(#(?:x|X)?[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]+);/g, (match, entityBody) => {
    if (entityBody.startsWith('#')) {
      return decodeNumericEntity(entityBody) ?? match
    }

    const decoded = NAMED_HTML_ENTITIES[entityBody.toLowerCase()]
    return decoded ?? match
  })
}

/**
 * Safely renders text that might contain HTML entities
 * @param text - The text to render
 * @returns The decoded text
 */
export function safeText(text: string | undefined | null): string {
  if (!text) return ''
  return decodeHtmlEntities(text)
}

/**
 * Strips HTML tags and normalizes whitespace/newlines into safe plain text.
 */
export function stripHtmlAndNormalize(text: string | undefined | null): string {
  if (!text) return ''

  const withBreaks = text
    .replace(/<\s*br\s*\/?>/gi, '\n')
    .replace(/<\/\s*p\s*>/gi, '\n')
    .replace(/<\/\s*div\s*>/gi, '\n')
    .replace(/<\/\s*li\s*>/gi, '\n')

  const container = document.createElement('div')
  container.innerHTML = withBreaks

  const raw = (container.textContent || container.innerText || '')
    .replace(/\u00a0/g, ' ')
    .replace(/\r\n?/g, '\n')
    .replace(/[ \t\f\v]+/g, ' ')
    .replace(/\n{3,}/g, '\n\n')
    .trim()

  return decodeHtmlEntities(raw)
}
