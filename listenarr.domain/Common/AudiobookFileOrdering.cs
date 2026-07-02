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

namespace Listenarr.Domain.Common
{
    /// <summary>
    /// Canonical ordering for the file list attached to an audiobook.
    ///
    /// EF returns audiobook files in essentially undefined (row-insertion / scan)
    /// order, which surfaces in the UI as a jumbled file list — "Disc 08, 01, 06,
    /// 10, 07, 09, 11, 12, 03..." for a 14-disc rip. Every API response that
    /// projects <see cref="Audiobook.Files"/> for the frontend should pass it
    /// through <see cref="InNaturalOrder"/> so the user sees a stable,
    /// human-natural sequence.
    ///
    /// Currently used by:
    ///  - <c>LibraryController.GetAudiobook</c> (the FE audiobook-detail endpoint)
    ///  - <c>AudiobookDtoFactory.BuildFromEntity</c> (scan SignalR broadcasts, move ops)
    /// </summary>
    public static class AudiobookFileOrdering
    {
        /// <summary>
        /// Return the supplied files sorted by filename using a natural-sort key
        /// (so "Disc 02" precedes "Disc 10"). Files without a path sink to the
        /// end so any caller defaulting to <c>files[0]</c> as a preview source
        /// still picks something playable. Returns an empty enumerable for null
        /// input — callers can chain <c>.Select(...).ToArray()</c> safely.
        /// </summary>
        public static IEnumerable<AudiobookFile> InNaturalOrder(IEnumerable<AudiobookFile>? source)
        {
            if (source == null) return Array.Empty<AudiobookFile>();

            return source
                .OrderBy(f => string.IsNullOrEmpty(f.Path) ? 1 : 0)
                .ThenBy(f => NaturalSort.ToKey(GetFileNameForSort(f.Path)), StringComparer.Ordinal)
                .ThenBy(f => f.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Id);
        }

        // Use just the filename for natural-sort purposes — directory separators in
        // the full path would otherwise dominate the key and group files by folder
        // instead of by name. Handles both forward and back slashes so Windows-rooted
        // paths from the importer sort the same as Unix paths from the scanner.
        private static string GetFileNameForSort(string? path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var lastSlash = path.LastIndexOfAny(new[] { '/', '\\' });
            return lastSlash < 0 ? path : path[(lastSlash + 1)..];
        }
    }
}
