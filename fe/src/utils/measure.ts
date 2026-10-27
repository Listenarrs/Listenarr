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
// Measures for layouts that CSS alone can't decide (what fits on one line)

// A length in pixels, 0 when the style has none
export function px(value: string): number {
  return parseFloat(value) || 0
}

// An element's gap between its columns
export function columnGap(element: Element): number {
  return px(getComputedStyle(element).columnGap)
}

// The width of an element's text on one line, even while it is wrapped: the sum of its lines
export function oneLineWidth(element: HTMLElement): number {
  const range = document.createRange()
  range.selectNodeContents(element)
  const lines = typeof range.getClientRects === 'function' ? Array.from(range.getClientRects()) : []
  return lines.length ? lines.reduce((width, line) => width + line.width, 0) : element.offsetWidth
}
