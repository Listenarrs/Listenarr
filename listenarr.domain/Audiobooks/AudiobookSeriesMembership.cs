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
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Listenarr.Domain.Audiobooks
{
    public class AudiobookSeriesMembership
    {
        [Key]
        public int Id { get; set; }

        public int AudiobookId { get; set; }

        [JsonIgnore]
        public Audiobook? Audiobook { get; set; }

        public string? SeriesName { get; set; }

        public string? SeriesNumber { get; set; }

        public string? SeriesAsin { get; set; }

        public bool IsPrimary { get; set; }

        public int SortOrder { get; set; }
    }
}
