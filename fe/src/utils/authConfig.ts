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

type AuthConfigLike = {
  authenticationRequired?: string | boolean | null
  AuthenticationRequired?: string | boolean | null
}

export function parseAuthRequiredValue(value: unknown): boolean | null {
  if (typeof value === 'boolean') return value

  if (typeof value === 'string') {
    const normalized = value.toLowerCase().trim()
    if (
      normalized === 'enabled' ||
      normalized === 'true' ||
      normalized === 'yes' ||
      normalized === '1'
    ) {
      return true
    }
    if (
      normalized === 'disabled' ||
      normalized === 'false' ||
      normalized === 'no' ||
      normalized === '0'
    ) {
      return false
    }
  }

  return null
}

export function parseAuthRequiredFromConfig(config: AuthConfigLike | null | undefined) {
  const raw = config?.authenticationRequired ?? config?.AuthenticationRequired
  return parseAuthRequiredValue(raw)
}
