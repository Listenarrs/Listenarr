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

// How found results are revealed: the line over them says what was found ("Found 3 results"),
// stays readable for holdMs, then fades out over fadeMs while the results come out of grey; they
// can't be picked until it is gone. Whether a request answered or the answered searches did (at
// once), the user sees the search was answered. A UX decision, open to discussion and meant for
// other screens too; tests set both to 0 (no reveal).
export const resultsReveal = { holdMs: 300, fadeMs: 200 }

// The whole reveal, from the answer until the results can be picked
export function revealMs(): number {
  return resultsReveal.holdMs + resultsReveal.fadeMs
}
