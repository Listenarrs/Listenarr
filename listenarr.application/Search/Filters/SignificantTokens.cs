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
using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Filters;

/// <summary>
/// Stop-word-filtered tokenization used by the automatic-search relevance gate. Centralizes
/// the notion of "what counts as a meaningful token": case, punctuation and Unicode-encoding
/// insensitivity, and dropping generic terms like "audiobook" that match every result.
///
/// Edition descriptors ("A Novel", "Unabridged Edition") are handled by
/// <see cref="FromTitle"/> at the segment level rather than as stop words, so a title that
/// genuinely contains such a word ("A Novel Obsession", "The Novel") keeps it.
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

    private static readonly Regex TitleSeparators = new(@"[:;,()\[\]：；，（）［］]|\s[-‐‑‒–—―−]\s", RegexOptions.Compiled);
    internal static readonly Regex CreditSeparators = new(@"[:;,()\[\]：；，（）［］\-‐‑‒–—―−]", RegexOptions.Compiled);
    private static readonly Regex TrailingEdition = new(@"\s+(?:a novel|(?:unabridged|abridged|novel|novels) edition)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> EditionDescriptors = new(StringComparer.OrdinalIgnoreCase)
    {
        "novel", "a novel", "novels", "edition", "unabridged", "abridged",
        "unabridged edition", "abridged edition",
    };

    /// <summary>
    /// Tokenizes a title, omitting edition descriptors that follow the main title: a separated
    /// segment that is nothing but a descriptor ("Dune: A Novel (Unabridged Edition)" → "dune")
    /// and a trailing descriptor phrase inside a segment ("Dune Unabridged Edition" → "dune").
    /// The first segment is always a title, and a descriptor word inside a longer segment is
    /// kept: "A Novel Obsession: A Novel (Unabridged Edition)" → "novel", "obsession".
    /// <paramref name="protectedName"/> (the stored main title, or a credited author) keeps a
    /// descriptor-shaped segment that is literally that name ("Dune - Novel" by the author
    /// "Novel"); with <paramref name="preserveProtectedName"/> that segment is also exempt from
    /// trailing-descriptor removal.
    /// </summary>
    public static IEnumerable<string> FromTitle(string? input, string? protectedName = null, bool preserveProtectedName = false)
    {
        if (string.IsNullOrWhiteSpace(input)) yield break;

        var hasTitle = false;
        var protectedSegments = TitleSeparators.Split(protectedName ?? string.Empty)
            .Select(part => Whitespace.Replace(part.Trim(), " "))
            .Where(segment => segment.Length > 0)
            .Where((segment, index) => index == 0 || !EditionDescriptors.Contains(segment))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var part in TitleSeparators.Split(input))
        {
            var segment = Whitespace.Replace(part.Trim(), " ");
            if (segment.Length == 0) continue;
            if (hasTitle && EditionDescriptors.Contains(segment)
                && !protectedSegments.Contains(segment)) continue;

            hasTitle = true;
            var title = preserveProtectedName && protectedSegments.Contains(segment)
                ? segment : WithoutTrailingEdition(segment);
            foreach (var token in From(title)) yield return token;
        }
    }

    private static string WithoutTrailingEdition(string segment)
    {
        // A multiword edition suffix can be present without punctuation. Only remove
        // it when a meaningful title remains; a title consisting of the phrase survives.
        while (true)
        {
            var suffix = TrailingEdition.Match(segment);
            if (!suffix.Success) return segment;
            var title = segment[..suffix.Index].TrimEnd();
            if (!From(title).Any()) return segment;
            segment = title;
        }
    }

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
