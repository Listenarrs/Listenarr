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
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { API_BASE_PATH } from '@/services/apiBase'

// Ensure we use the actual implementation (test setup globally mocks /services/api)
vi.unmock('@/services/api')
import { apiService as svc } from '@/services/api'

type FetchCall = [RequestInfo | URL, RequestInit?]
type FetchLikeMock = { mock: { calls: FetchCall[] } }

describe('ApiService.ensureImageCached', () => {
  const imageBasePath = `${API_BASE_PATH}/images`

  beforeEach(() => {
    vi.restoreAllMocks()
  })

  afterEach(() => {
    vi.resetAllMocks()
  })

  it('fetches the provided image endpoint so url parameters can populate the cache', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const s = String(input)
        if (s.includes(`${imageBasePath}/ASIN000001?url=`) && s.includes('audible.covers')) {
          return { ok: true, status: 200 }
        }
        return { ok: false, status: 404 }
      }),
    )

    const ok = await svc.ensureImageCached(
      `${imageBasePath}/ASIN000001?url=${encodeURIComponent('https://audible.covers/cover1.jpg')}`,
    )

    expect(ok).toBe(true)
    const fetchCalls = (globalThis.fetch as any as FetchLikeMock).mock.calls
    expect(fetchCalls.some((c) => String(c[0]).includes(`${imageBasePath}/ASIN000001?url=`))).toBe(
      true,
    )
  })

  it('checks the base image endpoint when no source url is provided', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const s = String(input)
        if (s.endsWith(`${imageBasePath}/ASIN000002`)) {
          return { ok: true, status: 200 }
        }
        return { ok: false, status: 404 }
      }),
    )

    const ok = await svc.ensureImageCached(`${imageBasePath}/ASIN000002`)

    expect(ok).toBe(true)
    const fetchCalls = (globalThis.fetch as any as FetchLikeMock).mock.calls
    expect(fetchCalls.some((c) => String(c[0]).endsWith(`${imageBasePath}/ASIN000002`))).toBe(true)
  })

  it('falls back to the base image endpoint when the provided endpoint fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const s = String(input)
        if (s.includes(`${imageBasePath}/ASIN000003?url=`)) {
          return { ok: false, status: 404 }
        }
        if (s.endsWith(`${imageBasePath}/ASIN000003`)) {
          return { ok: true, status: 200 }
        }
        return { ok: false, status: 404 }
      }),
    )

    const ok = await svc.ensureImageCached(
      `${imageBasePath}/ASIN000003?url=${encodeURIComponent('https://cached.example/cover3.jpg')}`,
    )

    expect(ok).toBe(true)
    const fetchCalls = (globalThis.fetch as unknown as FetchLikeMock).mock.calls
    expect(fetchCalls.some((c) => String(c[0]).includes(`${imageBasePath}/ASIN000003?url=`))).toBe(
      true,
    )
    expect(fetchCalls.some((c) => String(c[0]).endsWith(`${imageBasePath}/ASIN000003`))).toBe(true)
  })

  it('returns false when provided and base endpoints both fail', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const s = String(input)
        if (s.includes(`${imageBasePath}/ASIN000004?url=`)) {
          return { ok: false, status: 404 }
        }
        if (s.endsWith(`${imageBasePath}/ASIN000004`)) {
          return { ok: false, status: 404 }
        }
        return { ok: false, status: 404 }
      }),
    )

    const ok = await svc.ensureImageCached(
      `${imageBasePath}/ASIN000004?url=${encodeURIComponent('https://cached.example/cover4.jpg')}`,
    )

    expect(ok).toBe(false)
    const fetchCalls = (globalThis.fetch as unknown as FetchLikeMock).mock.calls
    expect(fetchCalls.some((c) => String(c[0]).includes(`${imageBasePath}/ASIN000004?url=`))).toBe(
      true,
    )
    expect(fetchCalls.some((c) => String(c[0]).endsWith(`${imageBasePath}/ASIN000004`))).toBe(true)
  })
})
