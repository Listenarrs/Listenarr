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
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import * as cache from '@/services/startupConfigCache'
import { apiService } from '@/services/api'
import { createDeferred } from '@/test/utils/wait'

// Mock apiService.getBootstrapConfig with a delayed resolver
let originalGet: unknown

beforeEach(() => {
  cache.resetCache()
  originalGet = (apiService as unknown as { getBootstrapConfig?: unknown }).getBootstrapConfig
})

afterEach(() => {
  ;(apiService as unknown as { getBootstrapConfig?: unknown }).getBootstrapConfig = originalGet
})

describe('startupConfigCache', () => {
  it('deduplicates concurrent calls', async () => {
    const pendingConfig = createDeferred<unknown>()
    ;(apiService as unknown as { getBootstrapConfig?: () => Promise<unknown> }).getBootstrapConfig =
      () => {
        return pendingConfig.promise
      }

    // Start multiple concurrent callers
    const callers = Promise.all([
      cache.getStartupConfigCached(),
      cache.getStartupConfigCached(),
      cache.getStartupConfigCached(),
    ])

    pendingConfig.resolve({ authenticationRequired: 'Enabled' })

    const results = await callers
    expect(results.length).toBe(3)
    // fetchCount should be exactly 1
    expect(cache.fetchCount).toBe(1)
  })
})
