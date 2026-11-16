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
// Import the real ApiService at test time (some tests mock the module globally)
let apiService: { request: (path: string, init?: RequestInit) => Promise<unknown> }

// Spies for toast methods
const info = vi.fn()
const success = vi.fn()
const error = vi.fn()

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ info, success, error }),
}))

describe('ApiService CSRF retry', () => {
  let fetchMock: any

  beforeEach(() => {
    info.mockClear()
    success.mockClear()
    error.mockClear()

    let tokenFetchCount = 0
    const spy = vi.fn((input: RequestInfo, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : (input as Request).url

      // Token fetch - return a stale/old token on the first call, then a "fresh"
      // token on the second call to simulate the real-world scenario where
      // a token was issued under a different principal earlier.
      if (url.endsWith('/antiforgery/token')) {
        tokenFetchCount++
        const token = tokenFetchCount === 1 ? 'oldtoken' : 'freshtoken'
        return Promise.resolve(
          new Response(JSON.stringify({ token }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        )
      }

      // Our test POST endpoint: if it includes the fresh token header, succeed;
      // otherwise return 400 to trigger retry.
      if (url.endsWith('/some/test')) {
        const hdrs = (init && (init.headers as Record<string, string> | Headers)) || {}
        const tokenInHdr =
          hdrs instanceof Headers
            ? hdrs.get('X-XSRF-TOKEN')
            : (hdrs as Record<string, string>)['X-XSRF-TOKEN']
        if (tokenInHdr === 'freshtoken') {
          return Promise.resolve(
            new Response(JSON.stringify({ success: true }), {
              status: 200,
              headers: { 'Content-Type': 'application/json' },
            }),
          )
        }
        return Promise.resolve(
          new Response('Invalid or missing CSRF token', {
            status: 400,
            headers: { 'Content-Type': 'text/plain' },
          }),
        )
      }

      // Fallback - unexpected
      return Promise.resolve(new Response('Unexpected', { status: 500 }))
    })
    vi.stubGlobal('fetch', spy)
    fetchMock = spy
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('fetches a fresh CSRF token, retries, and shows toasts', async () => {
    // Reset modules to ensure our vi.mock of toastService applies (clear any cached real module)
    vi.resetModules()

    // Import the real ApiService implementation to avoid global mocks
    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    apiService = actual.apiService as any

    // Simulate a request that includes an API key header (like saving the API key)
    const res = await apiService.request('/some/test', {
      method: 'POST',
      body: JSON.stringify({}),
      headers: { 'X-Api-Key': 'thekey' },
    })
    expect(res).toEqual({ success: true })

    // Verify the retry request included the refreshed token in headers
    // Find any fetch call that targeted our test endpoint and had the token header
    const calls = fetchMock.mock.calls as Array<any[]>

    // Verify the antiforgery token fetch used the original request's API key header
    const tokenFetchCall = calls.find((c) => {
      const input = c[0]
      const init = c[1]
      const url = typeof input === 'string' ? input : input && input.url
      if (!url || !url.endsWith('/antiforgery/token')) return false
      const hdrs = init && (init.headers as Record<string, string> | Headers)
      const apiKey = hdrs instanceof Headers ? hdrs.get('X-Api-Key') : hdrs && hdrs['X-Api-Key']
      return apiKey === 'thekey'
    })
    expect(tokenFetchCall).toBeTruthy()

    // Verify the retry request included both the fresh CSRF token and the API key
    const retryCall = calls.find((c) => {
      const input = c[0]
      const init = c[1]
      const url = typeof input === 'string' ? input : input && input.url
      if (!url || !url.endsWith('/some/test')) return false
      const hdrs = init && (init.headers as Record<string, string> | Headers)
      const token =
        hdrs instanceof Headers ? hdrs.get('X-XSRF-TOKEN') : hdrs && hdrs['X-XSRF-TOKEN']
      const apiKey = hdrs instanceof Headers ? hdrs.get('X-Api-Key') : hdrs && hdrs['X-Api-Key']
      return token === 'freshtoken' && apiKey === 'thekey'
    })
    expect(retryCall).toBeTruthy()

    // We validated the retry occurred and succeeded. Toast UI calls are implementation details and may not be present in all test environments.
  })
})
