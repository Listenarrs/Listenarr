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
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Picks the audio files whose opening/closing audio carries the spoken
    /// credits (ADR-0001). Multi-file books MUST use natural/track-number order
    /// ("Chapter 2" before "Chapter 10", via <see cref="AudiobookFileOrdering"/>)
    /// — alphabetical order would sample the middle of the book. On top of that,
    /// files named like front/back matter are forced to the play-order edges:
    /// "1984 - Appendix.mp3" sorts before "1984 - Chapter 1.mp3" alphabetically,
    /// but its audio is the END of the book, and a "Foreword" sorts after the
    /// chapters while its audio (and the credits) open the book.
    /// </summary>
    public static class VerificationFileSelection
    {
        // Keywords are matched on whole words in the file name AFTER the book's
        // own title is removed, so a book titled "The Epilogue" can't poison the
        // classification of its chapter files. Lists are deliberately
        // conservative: a missed keyword just means the old natural-order pick.
        private static readonly string[] FrontMatterKeywords =
        {
            "opening credits", "foreword", "preface", "prologue", "introduction", "dedication", "epigraph"
        };

        private static readonly string[] BackMatterKeywords =
        {
            "closing credits", "end credits", "appendix", "appendices", "afterword",
            "epilogue", "outro", "glossary", "acknowledgment", "acknowledgement",
            "acknowledgments", "acknowledgements", "about the author", "bonus", "interview", "excerpt", "preview"
        };

        /// <summary>
        /// The first and last audio file of the book in inferred play order
        /// (front matter → content in natural order → back matter), falling back
        /// to the legacy single <see cref="Audiobook.FilePath"/> when no file
        /// rows exist. Both null when the book has no audio at all.
        /// </summary>
        public static (string? FirstFile, string? LastFile) SelectFirstAndLast(Audiobook audiobook)
        {
            var ordered = AudiobookFileOrdering.InNaturalOrder(audiobook.Files)
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            if (ordered.Count > 0)
            {
                var byPlayOrder = ordered
                    .Select((path, index) => (path, index, rank: ClassifyPlayOrder(path!, audiobook.Title)))
                    .OrderBy(x => x.rank)
                    .ThenBy(x => x.index)
                    .Select(x => x.path)
                    .ToList();
                return (byPlayOrder[0], byPlayOrder[^1]);
            }

            var legacy = string.IsNullOrWhiteSpace(audiobook.FilePath) ? null : audiobook.FilePath;
            return (legacy, legacy);
        }

        /// <summary>0 = front matter, 1 = content, 2 = back matter.</summary>
        private static int ClassifyPlayOrder(string path, string? title)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(title))
            {
                name = name.Replace(title, " ", StringComparison.OrdinalIgnoreCase);
            }

            if (MatchesAny(name, FrontMatterKeywords)) return 0;
            if (MatchesAny(name, BackMatterKeywords)) return 2;
            return 1;
        }

        private static bool MatchesAny(string name, string[] keywords) =>
            keywords.Any(k => Regex.IsMatch(name, $@"\b{Regex.Escape(k)}\b", RegexOptions.IgnoreCase));
    }
}
