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

namespace Listenarr.Application.Search.Filters;

/// <summary>
/// Stop-word-filtered tokenization used by the relevance filter and the
/// backfill TitleMatcher. Centralized so the two share the same notion of
/// "what counts as a meaningful token" — e.g., punctuation/case insensitivity,
/// and dropping generic terms like "audiobook" that match every result.
/// </summary>
public static class SignificantTokens
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "in", "on", "at", "by", "to", "for",
        "with", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did",
        "but", "as", "if", "then", "so", "this", "that", "these", "those",
        "it", "its", "his", "her", "he", "she", "they", "them",
        "from", "into", "out", "up", "down",
        "audiobook", "audiobooks", "ebook", "ebooks", "book", "books",
    };

    private static readonly Regex TokenSplit = new(@"[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns the lowercased, stop-word-filtered tokens of length >= 2 in the input.
    /// </summary>
    public static IEnumerable<string> From(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) yield break;
        foreach (Match m in TokenSplit.Matches(input))
        {
            var t = m.Value.ToLowerInvariant();
            if (t.Length < 2) continue;
            if (StopWords.Contains(t)) continue;
            yield return t;
        }
    }
}
