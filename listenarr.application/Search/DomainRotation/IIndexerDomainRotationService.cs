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

namespace Listenarr.Application.Search.DomainRotation
{
    /// <summary>Result of a single domain-rotation check for one indexer.</summary>
    public record RotationResult(bool Changed, string? NewHost, string Reason);

    public interface IIndexerDomainRotationService
    {
        /// <summary>
        /// Verifies the indexer's current URL is reachable and, if not, rotates
        /// to the first working mirror domain and persists the change.
        /// Never throws; all errors are logged and returned as a no-change result.
        /// </summary>
        Task<RotationResult> EnsureReachableAsync(Indexer indexer, CancellationToken ct);
    }
}
