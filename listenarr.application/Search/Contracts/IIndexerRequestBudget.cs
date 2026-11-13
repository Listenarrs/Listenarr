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

namespace Listenarr.Application.Search.Contracts
{
    /// <summary>
    /// Per-indexer rolling 24-hour request budget. Every provider search call is charged
    /// before it is sent. Interactive searches may spend the whole
    /// <see cref="Indexer.DailyRequestLimit"/>; unattended ones stop short of it by the
    /// interactive reserve, so the sweep cannot use up what a person needs later in the day.
    /// Indexers without a limit are always admitted and not counted.
    /// </summary>
    public interface IIndexerRequestBudget
    {
        /// <summary>
        /// Admits and charges one request for <paramref name="origin"/>, or refuses it when that
        /// origin's share of the indexer's limit is used up. The check and the charge are atomic.
        /// </summary>
        Task<bool> TryAcquireAsync(Indexer indexer, SearchOrigin origin, int interactiveReservePercent);

        /// <summary>
        /// False only when every one of <paramref name="indexers"/> has a limit and has used up
        /// its sweep share, so a sweep search now could not send a single request.
        /// </summary>
        Task<bool> HasSweepHeadroomAsync(IReadOnlyCollection<Indexer> indexers, int interactiveReservePercent);
    }
}
