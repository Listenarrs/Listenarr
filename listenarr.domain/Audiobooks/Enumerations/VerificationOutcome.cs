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
using System.Text.Json.Serialization;

namespace Listenarr.Domain.Audiobooks.Enumerations
{
    /// <summary>
    /// Aggregate outcome of an identity-verification pass (ADR-0001).
    /// Maps onto VerificationStatus: Match → AgentVerified, Mismatch → AgentFlagged,
    /// Uncertain → AgentFlagged-for-review (surfaced in the "Needs review" view),
    /// NoSpokenCredits → AgentUnverifiable (neutral — the audio never announces
    /// itself, which is not evidence of wrong content).
    /// This acts as a DTO too for the API layer.
    /// </summary>
    public enum VerificationOutcome
    {
        [JsonStringEnumMemberName("uncertain")]
        Uncertain = 0,
        [JsonStringEnumMemberName("match")]
        Match = 1,
        [JsonStringEnumMemberName("mismatch")]
        Mismatch = 2,
        /// <summary>
        /// Plenty of clear speech, but the transcript contains no credit-shaped
        /// claims at all — nothing to corroborate OR contradict the metadata.
        /// A confident Mismatch requires spoken credits that contradict; their
        /// mere absence lands here instead.
        /// </summary>
        [JsonStringEnumMemberName("noSpokenCredits")]
        NoSpokenCredits = 3
    }
}
