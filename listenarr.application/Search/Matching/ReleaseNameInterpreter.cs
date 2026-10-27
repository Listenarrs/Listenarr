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
    private const string MetadataToken = @"(?:a novel|novels?|edition|unabridged|abridged|m4b|mp3|flac|aac|opus|audiobook|audio book|\d{4})";
    private static readonly Regex Metadata = new(
        "^" + MetadataToken + @"(?:[\s.!。]+" + MetadataToken + @")*[\s.!。]*$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const int MaxText = 16384;
    private const int MaxOccurrences = 32;
    private const int MaxExplanationWork = 1_000_000;

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
        extra = IdentityText.Canonical(extra).Trim(' ', '\t', '-', '‐', '‑', '‒', '–', '—', '―', '−', ':', ';', ',', '[', ']', '(', ')');
        if (extra.Length == 0) return (true, false);
        var remainingWork = MaxExplanationWork;
        var people = new List<(string Name, int ContentLength)>();
        foreach (var name in names)
        {
            if ((remainingWork -= name.Length) < 0) return (false, false);
            people.Add((name, IdentityText.PersonKey(name, false).Count(c => !char.IsWhiteSpace(c))));
        }
        var maxNameContent = people.Count == 0 ? 0 : people.Max(person => person.ContentLength);
        var pending = new Queue<(int Position, bool Credited, bool MetadataSeen)>();
        var visited = new HashSet<(int Position, bool Credited, bool MetadataSeen)>();
        var explained = false;
        Add(0, false, false);
        while (pending.TryDequeue(out var state))
        {
            if (--remainingWork < 0) return (false, false);
            if (state.Position == extra.Length)
            {
                if (state.Credited) return (true, true);
                explained = true;
                continue;
            }

            // Metadata cannot cross a structural separator. Assess its whole chunk once.
            var metadataEnd = state.Position;
            while (metadataEnd < extra.Length && !IsExplanationSeparator(extra[metadataEnd]))
            {
                if (--remainingWork < 0) return (false, false);
                metadataEnd++;
            }
            if ((remainingWork -= metadataEnd - state.Position) < 0) return (false, false);
            var metadataPart = extra[state.Position..metadataEnd].Trim();
            if (Metadata.IsMatch(metadataPart)) Add(metadataEnd, state.Credited, true);
            else if (state.MetadataSeen && metadataPart.Length > 0 && metadataPart.All(c => c is '.' or '!' or '。'))
                Add(metadataEnd, state.Credited, true);
            if (remainingWork < 0) return (false, false);

            // PersonEqual removes only periods and spacing. Stop probing once the span
            // contains more other characters than any complete known name can contain.
            var contentLength = 0;
            for (var end = state.Position + 1; end <= extra.Length; end++)
            {
                if (--remainingWork < 0) return (false, false);
                if (extra[end - 1] != '.' && !char.IsWhiteSpace(extra[end - 1])) contentLength++;
                if (contentLength > maxNameContent || people.Count == 0) break;
                if (end < extra.Length && !IsExplanationSeparator(extra[end])) continue;
                if ((remainingWork -= end - state.Position) < 0) return (false, false);
                var part = extra[state.Position..end].Trim();
                foreach (var person in people)
                {
                    if (--remainingWork < 0) return (false, false);
                    if (contentLength != person.ContentLength) continue;
                    if ((remainingWork -= part.Length + person.Name.Length) < 0) return (false, false);
                    if (!IdentityText.PersonEqual(person.Name, part)) continue;
                    var isCredited = state.Credited;
                    foreach (var author in credited)
                    {
                        if ((remainingWork -= part.Length + author.Length) < 0) return (false, false);
                        if (IdentityText.PersonEqual(author, part)) { isCredited = true; break; }
                    }
                    Add(end, isCredited, state.MetadataSeen);
                    break;
                }
                if (remainingWork < 0) return (false, false);
            }
        }
        return (explained, false);

        void Add(int position, bool author, bool metadata)
        {
            while (position < extra.Length && (char.IsWhiteSpace(extra[position]) || IsExplanationSeparator(extra[position])))
            {
                if (--remainingWork < 0) return;
                position++;
            }
            if (visited.Add((position, author, metadata))) pending.Enqueue((position, author, metadata));
        }
    }

    private static bool IsExplanationSeparator(char value) => "[]();:,-‐‑‒–—―−".Contains(value);
}
