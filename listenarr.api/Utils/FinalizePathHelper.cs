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
using System.Text.RegularExpressions;

namespace Listenarr.Api.Utils
{
    public static class FinalizePathHelper
    {
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray());
            cleaned = Regex.Replace(cleaned, "[^A-Za-z0-9 _-]+", " ");
            cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) return "unknown";
            return cleaned;
        }

        /// <summary>
        /// Build a deterministic destination path for multi-file releases using
        /// OutputPath/Author[/Series]/Title semantics.
        /// </summary>
        public static string BuildMultiFileDestination(ApplicationSettings settings, Download download, string fallbackDirName)
        {
            var outRoot = settings?.OutputPath;
            if (string.IsNullOrWhiteSpace(outRoot)) outRoot = "./completed";

            // Prefer explicit fields, fall back to metadata dictionary. Be forgiving with metadata keys.
            string? author = null;
            if (!string.IsNullOrWhiteSpace(download?.Artist)) author = download.Artist;
            if (string.IsNullOrWhiteSpace(author) && download?.Metadata != null)
            {
                // Try several possible keys that may contain author information
                foreach (var key in new[] { "Author", "artist", "Artist", "Authors", "AlbumArtist" }.Where(k => download.Metadata.TryGetValue(k, out _) && download.Metadata[k] != null))
                {
                    author = download.Metadata[key].ToString();
                    break;
                }
            }

            string? series = null;
            if (!string.IsNullOrWhiteSpace(download?.Series)) series = download.Series;
            if (string.IsNullOrWhiteSpace(series) && download?.Metadata != null)
            {
                // Try several keys that repositories may use for series/collection
                foreach (var key in new[] { "Series", "series", "SeriesTitle", "Album", "Collection", "Subtitle" }.Where(k => download.Metadata.TryGetValue(k, out _) && download.Metadata[k] != null))
                {
                    series = download.Metadata[key].ToString();
                    break;
                }
            }

            // Title: prefer explicit title, then fallback directory name
            string title = !string.IsNullOrWhiteSpace(download?.Title) ? download.Title : fallbackDirName ?? "import";

            // If author is still missing, attempt to heuristically split title values like "Author - Title"
            if (string.IsNullOrWhiteSpace(author) && !string.IsNullOrWhiteSpace(title))
            {
                var sep = " - ";
                if (title.Contains(sep))
                {
                    var splitParts = title.Split(new[] { sep }, 2, StringSplitOptions.None);
                    if (splitParts.Length == 2)
                    {
                        author = splitParts[0].Trim();
                        title = splitParts[1].Trim();
                    }
                }
                else if (title.Contains(":"))
                {
                    var splitParts = title.Split(new[] { ':' }, 2);
                    if (splitParts.Length == 2)
                    {
                        author = splitParts[0].Trim();
                        title = splitParts[1].Trim();
                    }
                }
            }

            var parts = new List<string>();
            parts.Add(outRoot);

            // Author folder
            var authorPart = SafeFileName(author ?? "Unknown Author");
            parts.Add(authorPart);

            // Series folder (optional)
            if (!string.IsNullOrWhiteSpace(series))
            {
                parts.Add(SafeFileName(series));
            }

            // Title folder
            parts.Add(SafeFileName(title));

            return Path.Join(parts.ToArray());
        }
    }
}


