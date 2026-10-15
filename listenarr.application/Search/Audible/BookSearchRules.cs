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

namespace Listenarr.Application.Search.Audible
{
    /// <summary>What to search on Audible for a book: its ASIN, or a title with an optional author.</summary>
    public sealed class BookSearchQuery
    {
        public string? Asin { get; init; }
        public string? Title { get; init; }
        public string? Author { get; init; }
    }

    /// <summary>
    /// The rules shared by every book search (Add New, Library Import's Start Matching and Find Match,
    /// Repair): what counts as an ASIN, which name a file or folder is searched by, and which result an
    /// automatic match may take.
    /// </summary>
    public static partial class BookSearchRules
    {
        // An Audible ASIN: "B" and 9 letters or digits, or an ISBN-10 (9 digits and a digit or X), with at
        // least one digit, so a title such as "Alchemised" or "Bartimaeus" is not taken for an ASIN
        [GeneratedRegex(@"^(?=.*\d)(B[0-9A-Z]{9}|\d{9}[\dX])$", RegexOptions.IgnoreCase)]
        private static partial Regex AsinPattern();

        [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
        private static partial Regex BracketTags();

        [GeneratedRegex(@"\s*\((\d+)\)\s*$")]
        private static partial Regex TrailingVolume();

        [GeneratedRegex(@"\s+[-–—]\s+")]
        private static partial Regex PartSeparator();

        [GeneratedRegex(@"^(\d{1,3})(?:\s*[-.)]\s*|\s+)(.*)$")]
        private static partial Regex LeadingNumber();

        public static bool LooksLikeAsin(string? value) =>
            !string.IsNullOrWhiteSpace(value) && AsinPattern().IsMatch(value.Trim());

        // Words of a title, ignoring case, accents and punctuation; numbers lose their leading zeros so
        // "04" and "4" are the same word
        public static IReadOnlyList<string> TitleWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            var plain = AudibleRequestHelper.RemoveDiacritics(text).ToLowerInvariant();
            var words = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (var c in plain)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(c);
                }
                else if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
            }

            return words
                .Select(w => w.All(char.IsAsciiDigit) ? w.TrimStart('0') is { Length: > 0 } n ? n : "0" : w)
                .ToList();
        }

        /// <summary>
        /// The book name inside a folder or file name such as
        /// "04 Primal Hunter - Primal Hunter - Primal Hunter, Tome 4 - Zogarth":
        /// "[...]" and "{...}" tags are dropped, dots and underscores become spaces in a name without
        /// spaces, the name is split on " - ", a part that is the author is dropped, a leading number is
        /// the volume (taken off), a part whose words are all in another part is dropped, and the volume
        /// is added back when no number is left.
        /// </summary>
        public static string CleanBookName(string name, string? author = null)
        {
            var text = BracketTags().Replace(name ?? string.Empty, " ");
            if (!text.Trim().Any(char.IsWhiteSpace))
            {
                text = Regex.Replace(text, @"[._]+", " ");
            }

            text = Regex.Replace(text, @"\s+", " ").Trim();
            var trailing = TrailingVolume().Match(text);
            if (trailing.Success)
            {
                text = $"{text[..trailing.Index].Trim()} {trailing.Groups[1].Value}";
            }

            var authorWords = TitleWords(author).ToHashSet(StringComparer.Ordinal);
            var volume = string.Empty;
            var parts = new List<string>();
            foreach (var raw in PartSeparator().Split(text))
            {
                var part = raw.Trim();
                if (Regex.IsMatch(part, @"^\d{1,3}$"))
                {
                    if (volume.Length == 0) volume = int.Parse(part).ToString();
                    continue;
                }

                var leading = LeadingNumber().Match(part);
                if (leading.Success && leading.Groups[2].Value.Length > 0)
                {
                    if (volume.Length == 0) volume = int.Parse(leading.Groups[1].Value).ToString();
                    part = leading.Groups[2].Value.Trim();
                }

                var words = TitleWords(part);
                if (words.Count == 0) continue;
                if (authorWords.Count > 0 && words.All(authorWords.Contains)) continue;
                parts.Add(part);
            }

            // Drop a part whose words are all in a longer part; of two identical parts, keep the first
            var kept = new List<string>();
            for (var i = 0; i < parts.Count; i++)
            {
                var words = TitleWords(parts[i]);
                var dropped = false;
                for (var j = 0; j < parts.Count && !dropped; j++)
                {
                    if (j == i) continue;
                    var other = TitleWords(parts[j]).ToHashSet(StringComparer.Ordinal);
                    dropped = words.All(other.Contains) && (other.Count > words.Count || j < i);
                }

                if (!dropped) kept.Add(parts[i]);
            }

            var result = string.Join(' ', kept.Count > 0 ? kept : parts).Trim();
            if (result.Length == 0) return text;
            return volume.Length > 0 && !result.Any(char.IsDigit) ? $"{result} {volume}" : result;
        }

        /// <summary>
        /// The name a Library Import row is searched by: the folder name for a book of several audio files
        /// (their names are chapters or parts, whose numbering throws the search off), the file name for a
        /// single file, cleaned up. The title tag is not used: it is often the series name.
        /// </summary>
        public static string LibraryImportTitle(string fullPath, string relativePath, int fileCount, string? author)
        {
            var folderName = relativePath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? relativePath;
            var fileName = Path.GetFileNameWithoutExtension(fullPath.Replace('\\', '/').Split('/').Last());
            var name = fileCount > 1 || string.IsNullOrWhiteSpace(fileName) ? folderName : fileName;
            return CleanBookName(name.Trim(), author);
        }

        /// <summary>
        /// What Start Matching searches for a Library Import row and what Find Match opens with: the ASIN
        /// read from the files when there is one, and always the cleaned name with the author from the tags.
        /// </summary>
        public static BookSearchQuery LibraryImportQuery(
            string fullPath,
            string relativePath,
            int fileCount,
            string? taggedAuthor,
            string? taggedAsin)
        {
            var author = string.IsNullOrWhiteSpace(taggedAuthor) ? null : taggedAuthor.Trim();
            return new BookSearchQuery
            {
                Asin = LooksLikeAsin(taggedAsin) ? taggedAsin!.Trim().ToUpperInvariant() : null,
                Title = LibraryImportTitle(fullPath, relativePath, fileCount, author),
                Author = author
            };
        }

        /// <summary>
        /// Whether a result is plausibly the book searched by name. Audible's keyword search returns loose
        /// results ("not a match" finds "The Perfect Match"), so an automatic match needs every word of the
        /// result's title in the searched name, or every searched word in its title, subtitle or series.
        /// </summary>
        public static bool TitleFits(
            string? searchedTitle,
            string? title,
            string? subtitle,
            IEnumerable<string?>? seriesParts)
        {
            var searched = TitleWords(searchedTitle).ToHashSet(StringComparer.Ordinal);
            var titleWords = TitleWords(title);
            if (searched.Count == 0 || titleWords.Count == 0) return false;
            if (titleWords.All(searched.Contains)) return true;

            var record = new[] { title, subtitle }
                .Concat(seriesParts ?? Array.Empty<string?>())
                .SelectMany(TitleWords)
                .ToHashSet(StringComparer.Ordinal);
            return searched.All(record.Contains);
        }

        /// <summary>A search result as the automatic match sees it.</summary>
        public sealed record MatchCandidate(
            string? Asin,
            string? Title,
            string? Subtitle,
            IReadOnlyList<string?> SeriesParts,
            string? FirstAuthor);

        /// <summary>
        /// The result Start Matching takes, or none: a result found by ASIN is the book; a result found by
        /// name must fit the name, and among those the first whose author fits the author read from the
        /// files wins, else the first one.
        /// </summary>
        public static int PickAutoMatch(IReadOnlyList<MatchCandidate> results, BookSearchQuery query)
        {
            if (!string.IsNullOrWhiteSpace(query.Asin))
            {
                return results.Count > 0 ? 0 : -1;
            }

            var fitting = results
                .Select((r, i) => (r, i))
                .Where(x => TitleFits(query.Title, x.r.Title, x.r.Subtitle, x.r.SeriesParts))
                .ToList();
            var needle = query.Author?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(needle))
            {
                foreach (var (r, i) in fitting)
                {
                    var name = r.FirstAuthor?.Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(name) && (name.Contains(needle) || needle.Contains(name)))
                    {
                        return i;
                    }
                }
            }

            return fitting.Count > 0 ? fitting[0].i : -1;
        }
    }
}
