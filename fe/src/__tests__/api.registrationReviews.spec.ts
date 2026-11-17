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
import { describe, it, expect, vi, afterEach } from 'vitest'

const reviews = [{ operationId: 'a' }, { operationId: 'b' }]

const listWith = async (headers: Record<string, string>, skip?: number) => {
  vi.resetModules()
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(reviews), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ...headers },
        }),
      ),
    ),
  )
  const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
  return actual.apiService.getRegistrationReviews(skip)
}

describe('ApiService getRegistrationReviews', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('reads the total from the X-Total-Count header', async () => {
    const result = await listWith({ 'X-Total-Count': '250' })
    expect(result.reviews).toHaveLength(2)
    expect(result.total).toBe(250)
  })

  it.each([
    ['zero', { 'X-Total-Count': '0' }],
    ['smaller than the rows returned', { 'X-Total-Count': '1' }],
  ])(
    'never reports fewer than the rows returned when the header is %s',
    async (_label, headers) => {
      const result = await listWith(headers)
      expect(result.total).toBe(2)
    },
  )

  it.each([
    ['absent', {}],
    ['empty', { 'X-Total-Count': '' }],
    ['blank', { 'X-Total-Count': '  ' }],
    ['non-integer', { 'X-Total-Count': 'many' }],
    ['fractional', { 'X-Total-Count': '1.5' }],
    ['negative', { 'X-Total-Count': '-3' }],
  ])('falls back to the row count when the header is %s', async (_label, headers) => {
    const result = await listWith(headers)
    expect(result.total).toBe(2)
  })

  it('sends skip only when it is positive', async () => {
    await listWith({}, 200)
    expect(String(vi.mocked(fetch).mock.calls[0][0])).toContain(
      '/file-registration-recovery?skip=200',
    )
    await listWith({})
    expect(String(vi.mocked(fetch).mock.calls[0][0])).not.toContain('skip')
  })
})
