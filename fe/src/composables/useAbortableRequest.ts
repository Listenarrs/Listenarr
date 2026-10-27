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
  | { status: 'failed'; error: unknown; timedOut: boolean }
  | { status: 'stale' }

export function useAbortableRequest(timeoutMs: number) {
  let abortController: AbortController | null = null

  async function run<T>(request: (signal: AbortSignal) => Promise<T>): Promise<RequestOutcome<T>> {
    const current = new AbortController()
    abortController = current
    let timedOut = false
    const timer = setTimeout(() => {
      timedOut = true
      current.abort()
    }, timeoutMs)
    try {
      const value = await request(current.signal)
      return abortController === current ? { status: 'done', value } : { status: 'stale' }
    } catch (error) {
      return abortController === current
        ? { status: 'failed', error, timedOut }
        : { status: 'stale' }
    } finally {
      clearTimeout(timer)
      if (abortController === current) abortController = null
    }
  }

  function stop() {
    abortController?.abort()
    abortController = null
  }

  return { run, stop }
}
