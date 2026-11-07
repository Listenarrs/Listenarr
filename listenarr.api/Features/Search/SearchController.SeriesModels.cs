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
namespace Listenarr.Api.Features.Search
{
    /// <summary>
    /// A series entity matching a name search, for callers that want to act on the series itself
    /// (for example to monitor it) rather than on the books inside it.
    /// </summary>
    public sealed class AudibleSeriesSearchItem
    {
        public string? Asin { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Region { get; set; }

        public string? Description { get; set; }

        public string? Image { get; set; }
    }
}
