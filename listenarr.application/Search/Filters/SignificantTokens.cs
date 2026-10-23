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
using System.Globalization;
using System.Text;

namespace Listenarr.Application.Search.Filters;

/// <summary>
/// Stop-word-filtered tokenization used by the automatic-search relevance gate. Centralizes
/// the notion of "what counts as a meaningful token": case, punctuation and Unicode-encoding
/// insensitivity, and dropping generic terms like "audiobook" that match every result.
///
/// Normalization is aligned with <c>IndexerQuerySanitizer</c>, which builds the query the
/// indexer answered: hyphens split words ("Full-Cast" → "full", "cast") and apostrophes are
/// dropped without splitting ("Ender's" → "enders"), so a release named after the sanitized
/// query ("Enders Game") tokenizes exactly like the stored title ("Ender's Game").
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

    /// <summary>
    /// Returns the lowercased, stop-word-filtered tokens of length >= 2 in the input.
    /// Tokens are runs of Unicode letters, digits and combining marks in any script. The
    /// text is composed (NFC) first, so precomposed and decomposed spellings ("é" vs "e" + U+0301) produce the same token; marks that
    /// have no precomposed form stay part of their token rather than splitting it.
    /// </summary>
    public static IEnumerable<string> From(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) yield break;

        var normalized = input.Normalize(NormalizationForm.FormC);
        var current = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (IsApostrophe(ch)) continue;

            if (char.IsLetterOrDigit(ch) || IsCombiningMark(ch))
            {
                current.Append(ch);
                continue;
            }

            if (current.Length > 0)
            {
                var token = Finish(current);
                if (token != null) yield return token;
            }
        }

        if (current.Length > 0)
        {
            var token = Finish(current);
            if (token != null) yield return token;
        }
    }

    private static string? Finish(StringBuilder current)
    {
        var token = current.ToString().ToLowerInvariant();
        current.Clear();
        if (token.Length < 2) return null;
        if (StopWords.Contains(token)) return null;
        return token;
    }

    // Straight, typographic and modifier-letter apostrophes — the same set the indexer query
    // sanitizer strips — join the surrounding letters instead of splitting them.
    private static bool IsApostrophe(char ch) =>
        ch is '\'' or '\u2018' or '\u2019' or '\u02bc';

    private static bool IsCombiningMark(char ch) =>
        CharUnicodeInfo.GetUnicodeCategory(ch)
            is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}
