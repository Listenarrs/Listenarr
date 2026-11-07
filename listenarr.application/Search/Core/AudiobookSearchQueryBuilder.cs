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

namespace Listenarr.Application.Search.Core;

/// <summary>
/// Builds the free text query handed to indexers for an audiobook.
/// </summary>
/// <remarks>
/// This is the single place an audiobook becomes a query string. Both the automatic
/// sweep and the download path call it, so the two cannot describe the same audiobook
/// differently. The stored <see cref="Audiobook.Title"/> stays untouched for display;
/// what goes on the wire is the derived query title from <see cref="BuildQueryTitle"/>.
/// The series is never part of the query. Readarr's BookSearchCriteria carries no series
/// field either, and a series name is often already in the title, so appending it mostly
/// repeated words.
/// </remarks>
public static class AudiobookSearchQueryBuilder
{
    /// <summary>
    /// Edition and format annotations that metadata providers append to a title.
    /// </summary>
    /// <remarks>
    /// Everything here describes how a recording was produced, not which work it is.
    /// Removing it does mean that editions of the same work, such as an abridged reading,
    /// an unabridged one and a dramatization, all send the same query. Annotations that
    /// do disambiguate, such as a part or volume number, are deliberately absent: losing
    /// those would turn a search for one half of a work into a search for either half.
    /// The list is closed rather than a pattern because a heuristic that guesses at
    /// annotations will eventually eat a real title.
    /// </remarks>
    private static readonly IReadOnlySet<string> EditionAnnotations =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "unabridged",
            "abridged",
            "unabridged edition",
            "abridged edition",
            "dramatized",
            "dramatised",
            "dramatized adaptation",
            "dramatised adaptation",
            "audio drama"
        };

    /// <summary>
    /// A parenthesised or bracketed span, captured without its delimiters.
    /// </summary>
    private static readonly Regex DelimitedSpan = new(
        @"\s*[\(\[]([^\(\)\[\]]*)[\)\]]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RepeatedWhitespace = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Build(Audiobook audiobook)
    {
        ArgumentNullException.ThrowIfNull(audiobook);

        var parts = new List<string>();

        var queryTitle = BuildQueryTitle(audiobook.Title);
        if (!string.IsNullOrEmpty(queryTitle))
        {
            parts.Add(queryTitle);
        }

        var author = audiobook.Authors?.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (!string.IsNullOrWhiteSpace(author))
        {
            parts.Add(author.Trim());
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Derives the query form of a stored display title.
    /// </summary>
    /// <remarks>
    /// Only delimited spans whose entire content is a known edition annotation are
    /// removed. A parenthesised span that is anything else is left alone, because it is
    /// far more likely to be part of the work's name than an artefact of the metadata
    /// provider, and a query missing real title words finds nothing at all.
    /// </remarks>
    public static string BuildQueryTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var stripped = DelimitedSpan.Replace(title, match =>
            EditionAnnotations.Contains(Collapse(match.Groups[1].Value))
                ? " "
                : match.Value);

        stripped = Collapse(stripped).Trim(' ', ',', ';', ':', '-');

        // Stripping must never empty a title. If the annotation was the whole thing,
        // the stored title is a better query than nothing.
        return stripped.Length == 0 ? Collapse(title) : stripped;
    }

    private static string Collapse(string value)
    {
        return RepeatedWhitespace.Replace(value, " ").Trim();
    }
}
