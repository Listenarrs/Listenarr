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
import { describe, expect, it } from 'vitest'
import { createAppRouter } from '@/router'

describe('Jobs route', () => {
  it('serves the Jobs page at /jobs, behind login', async () => {
    const route = createAppRouter().resolve('/jobs')

    expect(route.name).toBe('jobs')
    expect(route.meta.requiresAuth).toBe(true)
    const component = route.matched[0]?.components?.default as unknown as () => Promise<{
      default: { __name?: string }
    }>
    expect((await component()).default.__name).toBe('JobsView')
  })
})
