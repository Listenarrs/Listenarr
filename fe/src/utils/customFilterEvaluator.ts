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
import type { Audiobook } from '@/types'

export type RuleLike = {
  field: string
  operator: string
  value: string
  conjunction?: string
  groupStart?: boolean
  groupEnd?: boolean
}

function normalizeString(s: unknown) {
  return (s ?? '').toString().toLowerCase()
}

function toLocalDateKey(value: string | null | undefined): string | null {
  if (!value) return null

  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return null

  const year = date.getFullYear().toString().padStart(4, '0')
  const month = (date.getMonth() + 1).toString().padStart(2, '0')
  const day = date.getDate().toString().padStart(2, '0')
  return `${year}-${month}-${day}`
}

function normalizeDateRuleValue(value: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return null

  const [year, month, day] = value.split('-').map(Number)
  const date = new Date(Date.UTC(year, month - 1, day))
  if (
    date.getUTCFullYear() !== year ||
    date.getUTCMonth() !== month - 1 ||
    date.getUTCDate() !== day
  ) {
    return null
  }

  return value
}

function resolveFileCount(a: Audiobook): number {
  if (Array.isArray(a.files)) {
    return a.files.length
  }

  if (typeof a.fileCount === 'number' && Number.isFinite(a.fileCount)) {
    return a.fileCount
  }

  return 0
}

function evalSingle(a: Audiobook, r: RuleLike): boolean {
  const field = r.field
  const op = r.operator
  const val = (r.value || '').toString()
  let left = ''
  switch (field) {
    case 'monitored':
      left = String(!!a.monitored)
      break
    case 'title':
      left = String(a.title || '')
      break
    case 'author':
      left = (a.authors || []).map((x) => String(x)).join(' ')
      break
    case 'narrator':
      left = (a.narrators || []).map((x) => String(x)).join(' ')
      break
    case 'language':
      left = String(a.language || '')
      break
    case 'publisher':
      left = String(a.publisher || '')
      break
    case 'qualityProfileId':
      left = String(a.qualityProfileId ?? '')
      break
    case 'publishYear':
      left = String((a as unknown as Record<string, unknown>)['publishYear'] ?? '')
      break
    case 'publishedYear':
      left = String((a as unknown as Record<string, unknown>)['publishYear'] ?? '')
      break
    case 'path':
      left = String(
        (a as unknown as Record<string, unknown>)['filePath'] ||
          (a as unknown as Record<string, unknown>)['path'] ||
          '',
      )
      break
    case 'files':
      left = String(resolveFileCount(a))
      break
    case 'filesize':
      left = String((a as unknown as Record<string, unknown>)['fileSize'] ?? '')
      break
    case 'added':
      left = toLocalDateKey(a.added) ?? ''
      break
    default:
      left = String((a as unknown as Record<string, unknown>)[field] ?? '')
      break
  }

  const l = normalizeString(left)
  const v = normalizeString(val)

  if (field === 'added') {
    const leftDate = left || null
    const valueDate = normalizeDateRuleValue(val)
    if (!leftDate || !valueDate) return false

    switch (op) {
      case 'eq':
      case 'is':
        return leftDate === valueDate
      case 'ne':
      case 'is_not':
        return leftDate !== valueDate
      case 'lt':
        return leftDate < valueDate
      case 'lte':
        return leftDate <= valueDate
      case 'gt':
        return leftDate > valueDate
      case 'gte':
        return leftDate >= valueDate
      default:
        return true
    }
  }

  const numericFields = new Set(['publishYear', 'publishedYear', 'files', 'filesize'])
  if (numericFields.has(field)) {
    const leftNum = Number(left)
    const valNum = Number(val)
    if (isNaN(leftNum) || isNaN(valNum)) return false
    switch (op) {
      case 'eq':
        return leftNum === valNum
      case 'ne':
        return leftNum !== valNum
      case 'lt':
        return leftNum < valNum
      case 'lte':
        return leftNum <= valNum
      case 'gt':
        return leftNum > valNum
      case 'gte':
        return leftNum >= valNum
      case 'is':
        return leftNum === valNum
      case 'is_not':
        return leftNum !== valNum
      default:
        return true
    }
  }

  switch (op) {
    case 'is':
      return left === val
    case 'is_not':
      return left !== val
    case 'contains':
      return l.includes(v)
    case 'not_contains':
      return !l.includes(v)
    default:
      return true
  }
}

// Evaluate rules with parentheses and conjunctions (AND/OR). Rules array contains RuleLike
export function evaluateRules(a: Audiobook, rules: RuleLike[] | undefined): boolean {
  if (!rules || rules.length === 0) return true

  // Build token list: operands (boolean), operators 'AND'|'OR', parentheses '(' ')'
  const tokens: Array<boolean | 'AND' | 'OR' | '(' | ')'> = []

  rules.forEach((r, idx) => {
    // operator between previous and current is provided on this rule (conjunction)
    if (idx === 0) {
      if (r.groupStart) tokens.push('(')
      tokens.push(Boolean(evalSingle(a, r)))
      if (r.groupEnd) tokens.push(')')
    } else {
      const op = r.conjunction === 'or' ? 'OR' : 'AND'
      tokens.push(op)
      if (r.groupStart) tokens.push('(')
      tokens.push(Boolean(evalSingle(a, r)))
      if (r.groupEnd) tokens.push(')')
    }
  })

  // Shunting-yard to convert to postfix
  const output: Array<boolean | 'AND' | 'OR'> = []
  const ops: Array<'AND' | 'OR' | '('> = []

  const precedence = (op: 'AND' | 'OR') => (op === 'AND' ? 2 : 1)

  for (const t of tokens) {
    if (typeof t === 'boolean') {
      output.push(t)
    } else if (t === 'AND' || t === 'OR') {
      while (ops.length > 0) {
        const top = ops[ops.length - 1]
        if (top === '(') break
        if (precedence(top as 'AND' | 'OR') >= precedence(t as 'AND' | 'OR')) {
          output.push(ops.pop() as 'AND' | 'OR')
        } else break
      }
      ops.push(t)
    } else if (t === '(') {
      ops.push('(')
    } else if (t === ')') {
      while (ops.length > 0 && ops[ops.length - 1] !== '(') {
        output.push(ops.pop() as 'AND' | 'OR')
      }
      // pop the '('
      if (ops.length > 0 && ops[ops.length - 1] === '(') ops.pop()
    }
  }

  while (ops.length > 0) output.push(ops.pop() as 'AND' | 'OR')

  // Evaluate postfix
  const stack: boolean[] = []
  for (const t of output) {
    if (typeof t === 'boolean') stack.push(t)
    else {
      const b = stack.pop() ?? false
      const a2 = stack.pop() ?? false
      if (t === 'AND') stack.push(a2 && b)
      else stack.push(a2 || b)
    }
  }

  return stack.length > 0 ? stack[stack.length - 1]! : true
}

export default evaluateRules
