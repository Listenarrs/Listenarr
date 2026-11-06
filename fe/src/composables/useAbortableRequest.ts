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
// One request at a time that can be stopped: stop() aborts it, and its answer then goes nowhere
// (stale), as does the answer of a request a newer one replaced. One slower than timeoutMs is
// aborted and reported as timed out.

export type RequestOutcome<T> =
  | { status: 'done'; value: T }
  | { status: 'failed'; error: unknown; hasTimedOut: boolean }
  | { status: 'stale' }

export function useAbortableRequest(timeoutMs: number) {
  let abortController: AbortController | null = null

  async function run<T>(request: (signal: AbortSignal) => Promise<T>): Promise<RequestOutcome<T>> {
    const requestAbortController = new AbortController()
    abortController = requestAbortController
    let hasTimedOut = false
    const timeoutTimer = setTimeout(() => {
      hasTimedOut = true
      requestAbortController.abort()
    }, timeoutMs)
    try {
      const response = await request(requestAbortController.signal)
      return abortController === requestAbortController
        ? { status: 'done', value: response }
        : { status: 'stale' }
    } catch (error) {
      return abortController === requestAbortController
        ? { status: 'failed', error, hasTimedOut }
        : { status: 'stale' }
    } finally {
      clearTimeout(timeoutTimer)
      if (abortController === requestAbortController) abortController = null
    }
  }

  function stop() {
    abortController?.abort()
    abortController = null
  }

  return { run, stop }
}
