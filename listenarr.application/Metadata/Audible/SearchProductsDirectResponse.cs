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

using System.Text.Json;

namespace Listenarr.Application.Metadata.Audible
{
    internal sealed class SearchProductsDirectResponse
    {
        public List<AudibleSearchResult> Results { get; set; } = new();
        public int TotalResults { get; set; }
        public List<JsonElement>? RawProducts { get; set; }

        /// <summary>
        /// Audible did not answer, so an empty <see cref="Results"/> means "not known"
        /// rather than "not in the catalogue".
        ///
        /// Without this the two are the same object. A per-call timeout returns an empty
        /// response that is byte for byte what a genuine zero-match produces, and every
        /// caller downstream has to guess.
        /// </summary>
        public bool ProviderUnavailable { get; set; }
    }
}
