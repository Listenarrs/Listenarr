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
namespace Listenarr.Domain.Integrations
{
    public class AudiobookshelfConnectionSettings
    {
        public string Url { get; set; } = string.Empty;
        public string? ApiKey { get; set; }

        /// <summary>
        /// Optional Audiobookshelf library id to scan. When empty, every library
        /// with mediaType "book" is scanned.
        /// </summary>
        public string? LibraryId { get; set; }

        /// <summary>
        /// When true, Listenarr requests an Audiobookshelf scan automatically after
        /// files are imported into the library (downloads and manual uploads alike).
        /// </summary>
        public bool NotifyOnImport { get; set; }

        public bool HasSavedApiKey { get; set; }
    }
}
