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

namespace Listenarr.Application.Search.Contracts.Repositories
{
    /// <summary>
    /// Persistence for the hourly request counts behind <see cref="IIndexerRequestBudget"/>.
    /// Safe to call from a singleton: every call uses its own unit of work.
    /// </summary>
    public interface IIndexerRequestUsageStore
    {
        Task<IReadOnlyList<IndexerRequestUsage>> LoadSinceAsync(DateTime sinceHourUtc, CancellationToken ct = default);

        /// <summary>
        /// Writes the absolute count for one indexer-hour, inserting the row if it is new.
        /// </summary>
        Task SaveAsync(int indexerId, DateTime hourStartUtc, int count, CancellationToken ct = default);

        Task DeleteBeforeAsync(DateTime beforeHourUtc, CancellationToken ct = default);
    }
}
