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
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Listenarr.Infrastructure.Metadata.Parsing;

/// <summary>
/// Derives a folder-name matcher from the configured folder naming pattern, so scanning reads back
/// the layout the renamer writes instead of guessing at a fixed convention.
///
/// The matcher mirrors the renderer's elision rules: a bracket group containing only tokens
/// disappears when all of them are empty, and renders partially when only some are, so every token
/// inside such a group is independently optional here.
/// </summary>
internal static class NamingPatternFolderMatcher
{
    private static readonly ConcurrentDictionary<string, Regex?> Cache = new(StringComparer.Ordinal);

    private static readonly Regex TokenPattern = new(@"\{(\w+)\}", RegexOptions.Compiled);

    // Tolerates bracketed extras the pattern does not mention - a second series bracket, or a
    // trailing tag such as "[abridged]" - so one stray annotation does not lose the whole match.
    private const string AnyBracketGroup = @"(?:\s*\[[^\[\]]*\])";

    private static readonly Dictionary<char, char> Delimiters = new()
    {
        ['['] = ']',
        ['('] = ')',
        ['{'] = '}',
    };

    /// <summary>Token names that carry enough signal to call a directory a book folder.</summary>
    private static readonly string[] MarkerTokens =
        ["Series", "SeriesNumber", "Year", "Narrator", "Subtitle", "Edition", "Asin"];

    public static Regex? GetOrBuild(string? folderNamingPattern)
    {
        if (string.IsNullOrWhiteSpace(folderNamingPattern))
        {
            return null;
        }

        return Cache.GetOrAdd(folderNamingPattern, static pattern =>
        {
            try
            {
                var segments = pattern.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    return null;
                }

                // Only the last segment names the book folder; earlier segments are parent levels
                // that the caller already walks.
                var body = Convert(segments[^1], tokensOptional: false);

                // Allow unmentioned leading bracket groups immediately before the title.
                const string titleGroup = "(?<Title>";
                var titleIndex = body.IndexOf(titleGroup, StringComparison.Ordinal);
                if (titleIndex >= 0)
                {
                    body = body[..titleIndex] + AnyBracketGroup + @"*\s*" + body[titleIndex..];
                }

                return new Regex(
                    "^" + body + AnyBracketGroup + @"*\s*$",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                // An unparseable pattern must not break scanning; callers fall back to the
                // built-in convention.
                return null;
            }
        });
    }

    /// <summary>
    /// True when the match carries at least one non-title marker. Without this a bare directory
    /// name such as "Alastair Reynolds" satisfies the all-optional pattern and an author folder
    /// gets claimed as a book.
    /// </summary>
    public static bool HasBookFolderMarker(Match match)
    {
        foreach (var token in MarkerTokens)
        {
            var group = match.Groups[token];
            if (group.Success && !string.IsNullOrWhiteSpace(group.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Strips folder-pattern decoration from a value that is itself a rendered folder name.
    /// Taggers frequently write the whole rendered name into the album tag, which then reaches
    /// search as "[Revelation Space 10] Dilation Sleep" and matches nothing. When the value parses
    /// as the configured pattern, the bare title is returned; otherwise the value is left alone.
    /// </summary>
    public static string? ExtractTitle(string? value, string? folderNamingPattern)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var pattern = GetOrBuild(folderNamingPattern);
        if (pattern == null)
        {
            return value;
        }

        Match match;
        try
        {
            match = pattern.Match(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return value;
        }

        if (!match.Success || !HasBookFolderMarker(match))
        {
            return value;
        }

        var title = match.Groups["Title"];
        if (!title.Success)
        {
            return value;
        }

        var trimmed = title.Value.Trim();
        return trimmed.Length > 0 ? trimmed : value;
    }

    private static string Convert(string segment, bool tokensOptional)
    {
        var builder = new StringBuilder();
        var index = 0;

        while (index < segment.Length)
        {
            var token = TokenPattern.Match(segment, index);
            if (token.Success && token.Index == index)
            {
                builder.Append(RenderToken(token.Groups[1].Value, tokensOptional));
                index = token.Index + token.Length;
                continue;
            }

            var current = segment[index];
            if (Delimiters.TryGetValue(current, out var closing))
            {
                var depth = 1;
                var scan = index + 1;
                while (scan < segment.Length && depth > 0)
                {
                    if (segment[scan] == current) depth++;
                    else if (segment[scan] == closing) depth--;
                    scan++;
                }

                var inner = segment[(index + 1)..(scan - 1)];
                var innerTokens = TokenPattern.Matches(inner).Count;
                var onlyTokens = TokenPattern.Replace(inner, string.Empty).Trim().Length == 0;

                if (innerTokens > 0 && onlyTokens)
                {
                    builder.Append(@"(?:\s*")
                        .Append(Regex.Escape(current.ToString()))
                        .Append(Convert(inner, tokensOptional: true))
                        .Append(Regex.Escape(closing.ToString()))
                        .Append(")?");
                }
                else
                {
                    builder.Append(Regex.Escape(current.ToString()))
                        .Append(Convert(inner, tokensOptional))
                        .Append(Regex.Escape(closing.ToString()));
                }

                index = scan;
                continue;
            }

            // Literal whitespace becomes flexible so an elided group does not strand a separator.
            builder.Append(char.IsWhiteSpace(current) ? @"\s*" : Regex.Escape(current.ToString()));
            index++;
        }

        return builder.ToString();
    }

    private static string RenderToken(string name, bool optional)
    {
        var body = name switch
        {
            "Year" => @"\d{4}",
            "SeriesNumber" => @"[\d.]+",
            "DiskNumber" or "ChapterNumber" => @"\d+",
            _ => ".+?",
        };

        var group = $"(?<{name}>{body})";
        return optional ? $"(?:{group})?" : group;
    }
}
