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
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.unmock('../services/api')

describe('ApiService.downloadLogs', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('downloads logs through authenticated fetch when session auth is enabled', async () => {
    vi.resetModules()
    const { apiService } = await import('../services/api')
    const { sessionTokenManager } = await import('@/utils/sessionToken')

    sessionTokenManager.setToken('session-token')

    const originalCreateElement = document.createElement.bind(document)
    const anchor = originalCreateElement('a')
    const clickSpy = vi.spyOn(anchor, 'click').mockImplementation(() => {})
    const appendSpy = vi.spyOn(document.body, 'appendChild')
    const removeSpy = vi.spyOn(document.body, 'removeChild')
    const createElementSpy = vi
      .spyOn(document, 'createElement')
      .mockImplementation((tagName: string) => {
        if (tagName.toLowerCase() === 'a') {
          return anchor
        }

        return originalCreateElement(tagName)
      })

    const createObjectUrlSpy = vi
      .spyOn(URL, 'createObjectURL')
      .mockReturnValue('blob:listenarr-download')
    const revokeObjectUrlSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})

    const fetchSpy = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const headers = init?.headers as Record<string, string> | Headers | undefined
      const authHeader =
        headers instanceof Headers ? headers.get('Authorization') : headers?.Authorization

      expect(init?.credentials).toBe('include')
      expect(authHeader).toBe('Bearer session-token')

      return new Response(
        new ReadableStream({
          start(controller) {
            controller.enqueue(new TextEncoder().encode('log line 1'))
            controller.close()
          },
        }),
        {
          status: 200,
          headers: {
            'Content-Type': 'text/plain',
            'Content-Disposition': 'attachment; filename="listenarr-test.log"',
          },
        },
      )
    })

    vi.stubGlobal('fetch', fetchSpy)

    await apiService.downloadLogs()

    expect(fetchSpy).toHaveBeenCalledTimes(1)
    expect(createElementSpy).toHaveBeenCalledWith('a')
    expect(createObjectUrlSpy).toHaveBeenCalledTimes(1)
    expect(clickSpy).toHaveBeenCalledTimes(1)
    expect(anchor.download).toBe('listenarr-test.log')
    expect(anchor.href).toBe('blob:listenarr-download')
    expect(appendSpy).toHaveBeenCalledTimes(1)
    expect(removeSpy).toHaveBeenCalledTimes(1)
    expect(revokeObjectUrlSpy).toHaveBeenCalledWith('blob:listenarr-download')

    sessionTokenManager.clearToken()
  })
})
