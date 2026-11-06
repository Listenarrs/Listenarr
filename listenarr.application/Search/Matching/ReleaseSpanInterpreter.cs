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
    private sealed record State(int At, bool Credited, bool Metadata, Step? Path, IReadOnlyList<ReleaseSeriesClaim> Claims);
    internal sealed record Explanation(bool Complete, bool Credited, IReadOnlyList<ReleaseSpan> Spans, string? Reason)
    {
        public IReadOnlyList<ReleaseSeriesClaim> Claims { get; init; } = [];
    }

    public Explanation Explain(int start, int end, bool beforeTitle)
    {
        var names = context.Authors.Concat(supplied).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (Name: n, Role: ReleaseSpanRole.AuthorCredit, Credited: context.Authors.Any(a => IdentityText.PersonEqual(a, n))))
            .Concat((view.IsScene && (!beforeTitle || view.Text.AsSpan(start, end - start).Contains(',')) ? context.Narrators : []).Select(n =>
                (Name: n, Role: ReleaseSpanRole.NarratorCredit, Credited: false))).ToArray();
        var maxName = names.Length == 0 ? 0 : names.Max(n => n.Name.Length) * 2 + 16;
        var maxSeries = context.SeriesCatalog.MaxNameLength == 0 ? 0 : context.SeriesCatalog.MaxNameLength * 2 + 64;
        var pending = new Queue<State>();
        var seen = new HashSet<(int, bool, bool, string)>();
        string? conflict = null;
        State? complete = null;
        State? furthest = null;
        Add(start, false, false, null, []);
        while (pending.TryDequeue(out var state) && budget.Spend())
        {
            if (furthest == null || state.At > furthest.At) furthest = state;
            if (state.At == end)
            {
                complete = state;
                if (state.Credited) break;
                continue;
            }
            // A bare year-shaped field immediately following an unnumbered series
            // remains ambiguous; do not hide it inside a larger technical-token span.
            if (state.Path is { Span.Role: ReleaseSpanRole.SeriesName, Position: null }
                && state.At + 4 <= end && view.Text.AsSpan(state.At, 4).IndexOfAnyExceptInRange('0', '9') < 0
                && (state.At + 4 == end || char.IsWhiteSpace(view.Text[state.At + 4])
                    || ReleaseAnnotationGrammar.Separator(view.Text[state.At + 4])) && !StartsBracketed(state.At))
            {
                conflict ??= "numeric_annotation_ambiguous";
                continue;
            }
            // Reference-backed numbered series fields own their role before broader
            // annotation rules. Missing positions still carry release-side claims.
            if (ReserveSeries(state.At, out var reservedLength, out var reserved))
            {
                if (reservedLength > 0 && !HasFollowingPosition(state.At + reservedLength, end))
                    AddSeries(view.Text.Substring(state.At, reservedLength), reserved);
                continue;
            }
            if (budget.Exhausted) break;
            var chunkEnd = state.At;
            while (chunkEnd < end && !ReleaseAnnotationGrammar.Separator(view.Text[chunkEnd]) && budget.Spend()) chunkEnd++;
            if (!budget.Spend(chunkEnd - state.At)) break;
            var chunk = view.Text[state.At..chunkEnd].Trim();
            if ((state.Metadata || view.IsScene) && chunk.Length > 0 && chunk.All(c => c is '.' or '!' or '。'))
                AddSpan(chunkEnd, state.Credited, true, ReleaseSpanRole.TechnicalMetadata);

            var limit = Math.Min(end, state.At + Math.Max(ReleaseAnnotationRules.MaximumFieldLength, Math.Max(maxName + 12, maxSeries)));
            // None of the field recognizers may cross an annotation bracket.
            for (var boundary = state.At; boundary < limit && budget.Spend(); boundary++)
                if ("[]{}()".Contains(view.Text[boundary])) { limit = boundary; break; }
            for (var partEnd = state.At + 1; partEnd <= limit && budget.Spend(); partEnd++)
            {
                if (partEnd < end && !ReleaseAnnotationGrammar.Separator(view.Text[partEnd])
                    && !char.IsWhiteSpace(view.Text[partEnd])) continue;
                if (!budget.Spend(partEnd - state.At)) break;
                var part = view.Text[state.At..partEnd].TrimEnd();
                if (part.Length == 0) continue;
                var actualEnd = state.At + part.Length;
                if (part.Length <= ReleaseAnnotationRules.MaximumFieldLength && !part.Any(c => "[]{}()".Contains(c)))
                {
                    var rawStart = view.Span(state.At, part.Length, ReleaseSpanRole.Unknown).Start;
                    var signed = rawStart > 0 && "-−+".Contains(view.Original[rawStart - 1]);
                    var annotation = ReleaseAnnotationRules.Recognize(part, view.Text.AsSpan(actualEnd, end - actualEnd), context,
                        view.IsScene, beforeTitle, state.Metadata, IsBracketed(state.At, actualEnd), signed,
                        DelimitedPrefix(state.At, actualEnd), budget);
                    if (annotation.Explained)
                        Add(actualEnd, state.Credited, state.Metadata || annotation.Role == ReleaseSpanRole.TechnicalMetadata,
                            new(annotation.Span(view, state.At, part.Length), state.Path), state.Claims);
                    else
                    {
                        conflict ??= annotation.Reason;
                        if (annotation.Outcome == AnnotationOutcome.Ambiguous) continue;
                    }
                }
                // Names may cross their own commas/hyphens, never an annotation bracket.
                var raw = view.Raw(state.At, part.Length);
                if (part.Length <= maxName + 12 && !part.Any(c => "[]{}()".Contains(c)))
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
                            var narratorPrefix = name.Role == ReleaseSpanRole.NarratorCredit && beforeTitle
                                && CompleteNarratorPrefix(state.At, actualEnd, end);
                            if (name.Role == ReleaseSpanRole.NarratorCredit && beforeTitle && !narratorPrefix) continue;
                            var credit = name.Credited && (prefixLength > 0 || CreditBoundary(state.At, actualEnd)
                                || !ReleaseAnnotationGrammar.IsTechnical(person)
                                    && AnchoredWhitespaceCredit(state.At, actualEnd) || state.Credited
                                || !beforeTitle && state.Path?.Span.Role == ReleaseSpanRole.SeriesName
                                    && IsParenthetical(state.At, actualEnd)
                                    && !ReleaseAnnotationGrammar.PersonEqual(context.TitleParts?.Subtitle ?? string.Empty, person, view.IsScene));
                            Add(actualEnd, state.Credited || credit || narratorPrefix, state.Metadata,
                                new(view.Span(state.At, part.Length, name.Role), state.Path), state.Claims);
                        }
                    }
                }
                if (part.Length <= maxSeries && !part.Any(c => "[]{}()".Contains(c)))
                {
                    var annotation = ReleaseSeriesRule.Recognize(part, context.SeriesCatalog,
                        beforeTitle || IsBracketed(state.At, actualEnd) || ColonField(state.At) || FollowedByCreditedAuthor(actualEnd));
                    if (annotation.Explained)
                    {
                        if (!HasFollowingPosition(actualEnd, end)) AddSeries(part, annotation);
                    }
                    else conflict ??= annotation.Reason;
                }
            }

            void AddSpan(int to, bool credit, bool metadata, ReleaseSpanRole role) =>
                Add(to, credit, metadata, new(view.Span(state.At, chunkEnd - state.At, role), state.Path), state.Claims);

            void AddSeries(string part, ReleaseAnnotationResult annotation)
            {
                var span = annotation.Span(view, state.At, part.Length);
                ReleaseSpan? position = null;
                if (annotation.PositionClaims?.Count > 0 && ReleaseSeriesRule.PositionRange(part) is { } range)
                    position = view.Span(state.At + range.Start, range.Length, ReleaseSpanRole.SeriesPosition);
                var claims = state.Claims.Concat(annotation.PositionClaims ?? []).Distinct().ToArray();
                if (!budget.Spend(claims.Sum(c => c.Name.Length) + 1)) return;
                if (ReleaseSeriesClaim.Compatible(claims))
                    Add(state.At + part.Length, state.Credited, state.Metadata, new(span, state.Path, position), claims);
                else conflict ??= "series_position_conflict";
            }
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
        return new(true, complete.Credited, spans.AsReadOnly(), null) { Claims = complete.Claims };

        void Add(int position, bool credit, bool metadata, Step? path, IReadOnlyList<ReleaseSeriesClaim> claims)
        {
            while (position < end && (char.IsWhiteSpace(view.Text[position]) || ReleaseAnnotationGrammar.Separator(view.Text[position])))
            {
                if (!budget.Spend()) return;
                position++;
            }
            if (!budget.Spend(claims.Sum(c => c.Name.Length) + 1)) return;
            if (seen.Add((position, credit, metadata, claims.Count == 0 ? string.Empty : ReleaseSeriesClaim.Signature(claims))))
                pending.Enqueue(new(position, credit, metadata, path, claims));
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
                    && (char.IsWhiteSpace(view.Original[span.Start - 1])
                        || view.IsScene && view.Original[span.Start - 1] == '.');
        }

        bool ReserveSeries(int from, out int length, out ReleaseAnnotationResult reserved)
        {
            length = 0;
            reserved = default;
            var limit = Math.Min(end, from + maxSeries);
            for (var to = from + 1; to <= limit && budget.Spend(); to++)
            {
                if (view.Text[to - 1] is '[' or ']' or '(' or ')' or '{' or '}') break;
                if (to < end && !ReleaseAnnotationGrammar.Separator(view.Text[to]) && !char.IsWhiteSpace(view.Text[to])) continue;
                var part = view.Text[from..to].TrimEnd();
                if (part.Length == 0 || !budget.Spend(part.Length)) continue;
                var result = ReleaseSeriesRule.Recognize(part, context.SeriesCatalog,
                    beforeTitle || IsBracketed(from, from + part.Length) || ColonField(from) || FollowedByCreditedAuthor(from + part.Length));
                if ((result.Outcome is AnnotationOutcome.Conflict or AnnotationOutcome.Ambiguous)
                    && ReleaseSeriesRule.PositionRange(part) != null)
                {
                    length = 0;
                    conflict ??= result.Reason;
                    return true;
                }
                if (result.Explained && result.PositionClaims?.Count > 0)
                {
                    length = part.Length;
                    reserved = result;
                }
            }
            return length > 0;
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
                || view.Text[start - 1] == '[' && view.Text[end] == ']'
                || view.Text[start - 1] == '{' && view.Text[end] == '}');
    }

    private bool DelimitedPrefix(int start, int end)
    {
        var span = view.Span(start, end - start, ReleaseSpanRole.Unknown);
        if (!view.Original[..span.Start].All(char.IsWhiteSpace)) return false;
        var after = view.Original.AsSpan(span.Start + span.Length).TrimStart();
        return after.Length > 0 && "-‐‑‒–—―−".Contains(after[0]);
    }

    private bool CompleteNarratorPrefix(int start, int to, int end)
    {
        var span = view.Span(start, to - start, ReleaseSpanRole.NarratorCredit);
        var before = view.Original[..span.Start].TrimEnd(' ', '.', '\t');
        return before.EndsWith(',') && OnlySeparators(to, end)
            && context.Authors.Any(a => ReleaseAnnotationGrammar.PersonEqual(a, before[..^1].TrimEnd('.', ' '), view.IsScene));
    }

    private bool ColonField(int start)
    {
        var span = view.Span(start, 1, ReleaseSpanRole.Unknown);
        return view.Original[..span.Start].TrimEnd().EndsWith(':');
    }

    private bool IsParenthetical(int start, int end)
    {
        var span = view.Span(start, end - start, ReleaseSpanRole.Unknown);
        return view.Original[..span.Start].TrimEnd().EndsWith('(')
            && view.Original.AsSpan(span.Start + span.Length).TrimStart().StartsWith(")", StringComparison.Ordinal);
    }

    private bool StartsBracketed(int start)
    {
        var span = view.Span(start, 1, ReleaseSpanRole.Unknown);
        var before = view.Original[..span.Start].TrimEnd();
        return before.Length > 0 && "([{ ".Contains(before[^1]);
    }

    private bool FollowedByCreditedAuthor(int end)
    {
        if (end == 0) return false;
        var span = view.Span(end - 1, 1, ReleaseSpanRole.Unknown);
        var tail = view.Original.AsSpan(span.Start + span.Length).TrimStart();
        if (tail.Length == 0 || tail[0] != '(') return false;
        var close = tail.IndexOf(')');
        if (close is < 2 or > 256 || !budget.Spend(close)) return false;
        var name = tail[1..close].ToString();
        return context.Authors.Any(a => ReleaseAnnotationGrammar.PersonEqual(a, name, view.IsScene));
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
