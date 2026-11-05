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

namespace Listenarr.Domain.Search
{
    /// <summary>
    /// Who caused an indexer search. This is separate from the isAutomaticSearch flag, which
    /// only selects the indexer set and skips metadata scraping: the per-book search button and
    /// the background sweep both set that flag, but only the sweep is unattended.
    /// </summary>
    public enum SearchOrigin
    {
        /// <summary>
        /// A person asked for this search. It may spend an indexer's whole daily request limit,
        /// including the reserve.
        /// </summary>
        Interactive = 0,

        /// <summary>
        /// The automatic-search sweep. It stops at the share of the limit outside the reserve.
        /// </summary>
        Sweep = 1
    }
}
