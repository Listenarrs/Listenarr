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
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import type { UnmatchedFilesResponse } from '@/types'

export type UnmatchedScanJobOutcome =
  | { kind: 'completed'; response: UnmatchedFilesResponse }
  | { kind: 'failed'; error: string }
  | { kind: 'canceled' }

const POLL_INTERVAL_MS = 2500

// Follows one unmatched scan job until it ends: its SignalR event asks for its status at once, and
// polling covers a missed event. The outcome is reported once. Returns a function that stops
// following the job (the job itself goes on: canceling it is a separate call).
export function followUnmatchedScanJob(
  jobId: string,
  onEnd: (outcome: UnmatchedScanJobOutcome) => void,
): () => void {
  let stopped = false
  let checking = false
  let checkAgain = false

  const offSignalR = signalRService.onUnmatchedScanComplete((payload) => {
    if (payload.jobId === jobId) void check()
  })
  const pollTimer = setInterval(() => void check(), POLL_INTERVAL_MS)

  function stop() {
    stopped = true
    offSignalR()
    clearInterval(pollTimer)
  }

  function end(outcome: UnmatchedScanJobOutcome) {
    stop()
    onEnd(outcome)
  }

  async function check(): Promise<void> {
    if (stopped) return
    if (checking) {
      // An event arrived while a check was in flight: that check may predate the end
      checkAgain = true
      return
    }
    checking = true
    try {
      const response = await apiService.getUnmatchedResults(jobId)
      if (stopped) return
      if (response.status === 'Completed') end({ kind: 'completed', response })
      else if (response.status === 'Failed')
        end({ kind: 'failed', error: response.error ?? 'Scan failed' })
      else if (response.status === 'Canceled') end({ kind: 'canceled' })
    } catch (e) {
      if (stopped) return
      if ((e as { status?: number })?.status === 404) {
        end({
          kind: 'failed',
          error: 'The server no longer knows this scan (it may have restarted). Scan again.',
        })
      }
      // Anything else is transient: the next poll asks again
    } finally {
      checking = false
    }
    if (checkAgain && !stopped) {
      checkAgain = false
      await check()
    }
  }

  void check()
  return stop
}
