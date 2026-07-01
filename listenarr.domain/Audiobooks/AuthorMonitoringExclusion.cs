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

namespace Listenarr.Domain.Audiobooks
{
    /// <summary>
    /// A book the author-monitoring sweep must never re-add to the library.
    /// Written when the user deletes an audiobook and opts to keep it out of a
    /// monitored author's catalog sync: without it, deleting a book by a monitored
    /// author only triggered the next sweep to re-discover it as "missing" and add
    /// it straight back (then auto-search re-grabbed it).
    ///
    /// Identity mirrors how the sweep matches catalog books to the library
    /// (<c>FindExistingLibraryMatch</c>): a normalized ASIN when known, plus a
    /// normalized title+author key as a fallback for editions without an ASIN.
    /// <see cref="Title"/> and <see cref="AuthorName"/> are stored purely for
    /// display in the exclusions management UI.
    /// </summary>
    public class AuthorMonitoringExclusion
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Normalized ASIN (letters+digits, upper-case) when known — the strongest identity.</summary>
        public string? Asin { get; set; }

        /// <summary>Normalized title+author key, used to match editions that have no ASIN.</summary>
        public string? TitleAuthorKey { get; set; }

        /// <summary>Original book title, for display only.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Primary author name, for display only.</summary>
        public string? AuthorName { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
