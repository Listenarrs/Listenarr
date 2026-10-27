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

namespace Listenarr.Application.Search.Matching;

public sealed record ReleaseTitleMatch(bool Complete, bool Credited, string? Title, bool FoundTitle);

/// <summary>First protect a complete reference title; classify only text outside its span.</summary>
public static class ReleaseNameInterpreter
{
    private static readonly Regex Separators = new(@"[\[\]();:,]|[-‐‑‒–—―−]", RegexOptions.Compiled);
    private const string MetadataToken = @"(?:a novel|novels?|edition|unabridged|abridged|m4b|mp3|flac|aac|opus|audiobook|audio book|\d{4})";
    private static readonly Regex Metadata = new(
        "^" + MetadataToken + @"(?:[\s.!。]+" + MetadataToken + @")*[\s.!。]*$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const int MaxText = 16384;
    private const int MaxOccurrences = 32;

    public static ReleaseTitleMatch Match(string release, BookMatchContext context,
        IReadOnlyList<string> suppliedAuthors)
    {
        if (release.Length > MaxText || context.Titles.Count > 64 || context.Authors.Count > 64)
            return new(false, false, null, false);
        var names = context.Authors.Concat(suppliedAuthors).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parsedCredit = HasCreditBoundary(release, context.Authors);
        var found = false;
        var attempts = 0;
        foreach (var title in context.Titles)
        {
            foreach (var presentation in new[] { false, true })
            {
                var hay = presentation ? IdentityText.Presentation(release) : release;
                var needle = presentation ? IdentityText.Presentation(title) : title;
                for (var offset = 0; offset <= hay.Length - needle.Length;)
                {
                    var index = hay.IndexOf(needle, offset, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    offset = index + Math.Max(1, needle.Length);
                    if (++attempts > MaxOccurrences) return new(false, false, null, found);
                    if (!Boundary(hay, index - 1) || !Boundary(hay, index + needle.Length)) continue;
                    found = true;
                    var left = Explain(hay[..index], names, context.Authors);
                    var right = Explain(hay[(index + needle.Length)..], names, context.Authors);
                    if (left.Explained && right.Explained)
                        return new(true, parsedCredit && (left.Credited || right.Credited), title, true);
                }
            }
        }
        return new(false, false, null, found);
    }

    private static bool HasCreditBoundary(string release, IReadOnlyList<string> authors)
    {
        foreach (var normalizeInitialSpacing in new[] { false, true })
        {
            var hay = IdentityText.PersonKey(release, normalizeInitialSpacing);
            var attempts = 0;
            foreach (var author in authors)
            {
                var name = IdentityText.PersonKey(author, normalizeInitialSpacing);
                for (var offset = 0; offset <= hay.Length - name.Length && name.Length > 0;)
                {
                    if (++attempts > MaxOccurrences) break;
                    var at = hay.IndexOf(name, offset, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;
                    offset = at + name.Length;
                    var before = hay[..at].TrimEnd();
                    var after = hay[(at + name.Length)..].TrimStart();
                    if (before.Length == 0 && after.Length > 0 && "-‐‑‒–—―−".Contains(after[0])) return true;
                    if (before.Length > 0 && "-‐‑‒–—―−".Contains(before[^1]) &&
                        Explain(after, Array.Empty<string>(), Array.Empty<string>()).Explained) return true;
                }
                if (attempts > MaxOccurrences) break;
            }
        }
        return false;
    }

    private static bool Boundary(string value, int index) =>
        index < 0 || index >= value.Length || !char.IsLetterOrDigit(value[index])
            && char.GetUnicodeCategory(value[index]) is not
                (System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark);

    private static (bool Explained, bool Credited) Explain(string extra, IReadOnlyList<string> names,
        IReadOnlyList<string> credited)
    {
        extra = extra.Trim(' ', '\t', '-', '‐', '‑', '‒', '–', '—', '―', '−', ':', ';', ',', '[', ']', '(', ')');
        if (extra.Length == 0) return (true, false);
        // Check complete names before splitting punctuation: a name is never edition metadata.
        if (names.Any(n => IdentityText.PersonEqual(n, extra)))
            return (true, credited.Any(n => IdentityText.PersonEqual(n, extra)));
        var author = false;
        var metadataSeen = false;
        foreach (var part in Separators.Split(extra).Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (names.Any(n => IdentityText.PersonEqual(n, part)))
                author |= credited.Any(n => IdentityText.PersonEqual(n, part));
            else if (Metadata.IsMatch(part)) metadataSeen = true;
            else if (!metadataSeen || !part.All(c => c is '.' or '!' or '。')) return (false, false);
        }
        return (true, author);
    }
}
