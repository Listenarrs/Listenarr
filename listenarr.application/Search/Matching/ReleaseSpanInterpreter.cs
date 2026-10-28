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
namespace Listenarr.Application.Search.Matching;

internal sealed class ReleaseSpanInterpreter(
    ReleaseTextView view, BookMatchContext context, IReadOnlyList<string> supplied, ReleaseAssessmentBudget budget)
{
    private sealed record Step(ReleaseSpan Span, Step? Previous, ReleaseSpan? Position = null);
    private sealed record State(int At, bool Credited, bool Metadata, Step? Path);
    internal sealed record Explanation(bool Complete, bool Credited, IReadOnlyList<ReleaseSpan> Spans, string? Reason);

    public Explanation Explain(int start, int end, bool beforeTitle)
    {
        var names = context.Authors.Concat(supplied).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (Name: n, Role: ReleaseSpanRole.AuthorCredit, Credited: context.Authors.Any(a => IdentityText.PersonEqual(a, n))))
            .Concat((view.IsScene && !beforeTitle ? context.Narrators : []).Select(n =>
                (Name: n, Role: ReleaseSpanRole.NarratorCredit, Credited: false))).ToArray();
        var maxName = names.Length == 0 ? 0 : names.Max(n => n.Name.Length) * 2 + 16;
        var maxSeries = context.SeriesReferences.Count == 0 ? 0 : context.SeriesReferences.Max(s => s.Name.Length) * 2 + 64;
        var pending = new Queue<State>();
        var seen = new HashSet<(int, bool, bool)>();
        string? conflict = null;
        State? complete = null;
        State? furthest = null;
        Add(start, false, false, null);
        while (pending.TryDequeue(out var state) && budget.Spend())
        {
            if (furthest == null || state.At > furthest.At) furthest = state;
            if (state.At == end)
            {
                complete = state;
                if (state.Credited) break;
                continue;
            }
            var chunkEnd = state.At;
            while (chunkEnd < end && !ReleaseAnnotationGrammar.Separator(view.Text[chunkEnd]) && budget.Spend()) chunkEnd++;
            if (!budget.Spend(chunkEnd - state.At)) break;
            var chunk = view.Text[state.At..chunkEnd].Trim();
            if (ReleaseAnnotationGrammar.IsTechnical(chunk))
                AddSpan(chunkEnd, state.Credited, true, ReleaseSpanRole.TechnicalMetadata);
            else if ((state.Metadata || view.IsScene) && chunk.Length > 0 && chunk.All(c => c is '.' or '!' or '。'))
                AddSpan(chunkEnd, state.Credited, true, ReleaseSpanRole.TechnicalMetadata);
            if (ReleaseAnnotationGrammar.IsExplicitNarrator(chunk))
                AddSpan(chunkEnd, state.Credited, state.Metadata, ReleaseSpanRole.NarratorCredit);
            if (!beforeTitle && OnlySeparators(chunkEnd, end) && ReleaseAnnotationGrammar.IsGroup(chunk))
                AddSpan(chunkEnd, state.Credited, state.Metadata, ReleaseSpanRole.GroupMarker);

            var limit = Math.Min(end, state.At + Math.Max(maxName + 12, maxSeries));
            for (var partEnd = state.At + 1; partEnd <= limit && budget.Spend(); partEnd++)
            {
                if (partEnd < end && !ReleaseAnnotationGrammar.Separator(view.Text[partEnd])
                    && !char.IsWhiteSpace(view.Text[partEnd])) continue;
                if (!budget.Spend(partEnd - state.At)) break;
                var part = view.Text[state.At..partEnd].TrimEnd();
                if (part.Length == 0) continue;
                var actualEnd = state.At + part.Length;
                // Names may cross their own commas/hyphens, never an annotation bracket.
                var raw = view.Raw(state.At, part.Length);
                if (part.Length <= maxName + 12 && !part.Any(c => "[]()".Contains(c)))
                {
                    var labelLength = AuthorLabelLength(part);
                    // Cue lengths belong to the comparison view. Map the name range
                    // back to raw text, and retain the complete unlabelled-name path.
                    for (var form = 0; form < (labelLength > 0 ? 2 : 1); form++)
                    {
                        var prefixLength = form == 0 ? labelLength : 0;
                        if (prefixLength >= part.Length) continue;
                        var person = prefixLength == 0 ? raw : view.Raw(state.At + prefixLength, part.Length - prefixLength);
                        foreach (var name in names)
                        {
                            if (!budget.Spend(person.Length + name.Name.Length)) break;
                            if (!ReleaseAnnotationGrammar.PersonEqual(name.Name, person, view.IsScene)) continue;
                            var credit = name.Credited && (prefixLength > 0 || CreditBoundary(state.At, actualEnd)
                                || !ReleaseAnnotationGrammar.IsTechnical(person)
                                    && AnchoredWhitespaceCredit(state.At, actualEnd) || state.Credited);
                            Add(actualEnd, state.Credited || credit, state.Metadata,
                                new(view.Span(state.At, part.Length, name.Role), state.Path));
                        }
                    }
                }
                if (part.Length <= maxSeries && !part.Any(c => "[]()".Contains(c)))
                {
                    if (ReleaseAnnotationGrammar.Series(part, context, view.IsScene,
                        beforeTitle || IsBracketed(state.At, actualEnd), out var reason))
                    {
                        // A bare known series name cannot hide a following unbracketed position.
                        if (!HasFollowingPosition(actualEnd, end))
                        {
                            var span = view.Span(state.At, part.Length, ReleaseSpanRole.SeriesName);
                            ReleaseSpan? position = null;
                            if (ReleaseAnnotationGrammar.PositionRange(part) is { } range)
                                position = view.Span(state.At + range.Start, range.Length, ReleaseSpanRole.SeriesPosition);
                            Add(actualEnd, state.Credited, state.Metadata, new(span, state.Path, position));
                        }
                    }
                    else conflict ??= reason;
                }
            }

            void AddSpan(int to, bool credit, bool metadata, ReleaseSpanRole role) =>
                Add(to, credit, metadata, new(view.Span(state.At, chunkEnd - state.At, role), state.Path));
        }
        if (budget.Exhausted) return new(false, false, [], "assessment_budget_exceeded");
        var spans = new List<ReleaseSpan>();
        for (var step = (complete ?? furthest)?.Path; step != null; step = step.Previous)
        {
            spans.Add(step.Span);
            if (step.Position != null) spans.Add(step.Position);
        }
        if (complete == null)
        {
            var unknownStart = furthest?.At ?? start;
            if (unknownStart < end) spans.Add(view.Span(unknownStart, end - unknownStart, ReleaseSpanRole.Unknown));
            return new(false, false, spans.AsReadOnly(), conflict);
        }
        return new(true, complete.Credited, spans.AsReadOnly(), null);

        void Add(int position, bool credit, bool metadata, Step? path)
        {
            while (position < end && (char.IsWhiteSpace(view.Text[position]) || ReleaseAnnotationGrammar.Separator(view.Text[position])))
            {
                if (!budget.Spend()) return;
                position++;
            }
            if (seen.Add((position, credit, metadata))) pending.Enqueue(new(position, credit, metadata, path));
        }

        bool AnchoredWhitespaceCredit(int from, int to)
        {
            var span = view.Span(from, to - from, ReleaseSpanRole.AuthorCredit);
            // A complete known author occupies the first field on either side of the
            // protected title. Every remaining field still has to be explained.
            return beforeTitle
                ? view.Original[..span.Start].All(char.IsWhiteSpace)
                    && span.Start + span.Length < view.Original.Length
                    && char.IsWhiteSpace(view.Original[span.Start + span.Length])
                : OnlySeparators(start, from) && span.Start > 0
                    && char.IsWhiteSpace(view.Original[span.Start - 1]);
        }
    }

    private bool OnlySeparators(int start, int end)
    {
        for (var i = start; i < end; i++)
            if (!char.IsWhiteSpace(view.Text[i]) && !ReleaseAnnotationGrammar.Separator(view.Text[i])) return false;
        return true;
    }

    private bool IsBracketed(int start, int end)
    {
        while (start > 0 && char.IsWhiteSpace(view.Text[start - 1])) start--;
        while (end < view.Text.Length && char.IsWhiteSpace(view.Text[end])) end++;
        return start > 0 && end < view.Text.Length
            && (view.Text[start - 1] == '(' && view.Text[end] == ')'
                || view.Text[start - 1] == '[' && view.Text[end] == ']');
    }

    private bool CreditBoundary(int start, int end)
    {
        var span = view.Span(start, end - start, ReleaseSpanRole.AuthorCredit);
        var before = view.Original[..span.Start].TrimEnd(' ', '\t', '[', '(');
        var after = view.Original[(span.Start + span.Length)..].TrimStart(' ', '\t', ']', ')');
        return before.Length == 0 && after.Length > 0 && ("-‐‑‒–—―−".Contains(after[0]) || view.IsScene && after[0] == '.')
            || before.Length > 0 && "-‐‑‒–—―−".Contains(before[^1]);
    }

    private bool HasFollowingPosition(int start, int end)
    {
        var tail = view.Text[start..end].TrimStart(' ', ',', ':', '#');
        if (tail.Length == 0 || tail[0] is '[' or '(' || "-‐‑‒–—―−".Contains(tail[0])) return false;
        return char.IsDigit(tail[0]) || new[] { "Book ", "Bk ", "Vol ", "Volume ", "Part " }
            .Any(p => tail.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static int AuthorLabelLength(string value)
    {
        foreach (var prefix in new[] { "written by ", "by ", "author: " })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return prefix.Length;
        return 0;
    }
}
