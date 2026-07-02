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
    /// Identity-verification state of an audiobook (ADR-0001).
    /// Manual states (ManuallyVerified, Rejected) are sticky: an agent pass
    /// must never overwrite them — see <see cref="VerificationStatusExtensions.IsAgentWritable"/>.
    /// This acts as a DTO too for the API layer.
    /// </summary>
    public enum VerificationStatus
    {
        [JsonStringEnumMemberName("unverified")]
        Unverified = 0,
        [JsonStringEnumMemberName("agentVerified")]
        AgentVerified = 1,
        [JsonStringEnumMemberName("agentFlagged")]
        AgentFlagged = 2,
        [JsonStringEnumMemberName("manuallyVerified")]
        ManuallyVerified = 3,
        [JsonStringEnumMemberName("rejected")]
        Rejected = 4,
        /// <summary>
        /// The agent listened but the audio never announces itself — no spoken
        /// credits to match or contradict. NOT a flag: absence of credits is not
        /// evidence of wrong content (many books open with pure narration).
        /// </summary>
        [JsonStringEnumMemberName("agentUnverifiable")]
        AgentUnverifiable = 5
    }

    public static class VerificationStatusExtensions
    {
        /// <summary>
        /// True when an agent verification pass is allowed to replace this status.
        /// Human decisions (ManuallyVerified, Rejected) outrank agent verdicts.
        /// </summary>
        public static bool IsAgentWritable(this VerificationStatus status) =>
            status is not (VerificationStatus.ManuallyVerified or VerificationStatus.Rejected);
    }
}
