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

namespace Listenarr.Application.Downloads.Contracts
{
    /// <summary>
    /// What one monitor poll of a download client said beyond the updated downloads: which of
    /// them the client listed, and whether that answer can be trusted to be the whole of it.
    /// </summary>
    /// <remarks>
    /// An unreported download comes back from the poll unchanged, so the returned list cannot
    /// show that it was missing. Missing means removed only when <see cref="IsComplete"/> holds:
    /// an adapter that skips an item it cannot read leaves it out while the client still has it.
    /// </remarks>
    public sealed class DownloadPollReport
    {
        /// <summary>The ids of the downloads the client listed in this poll.</summary>
        public ISet<string> ReportedDownloadIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// False when the client listed items the adapter could not read and left out, so a
        /// download missing from <see cref="ReportedDownloadIds"/> may still be in the client.
        /// </summary>
        public bool IsComplete { get; set; } = true;
    }
}
