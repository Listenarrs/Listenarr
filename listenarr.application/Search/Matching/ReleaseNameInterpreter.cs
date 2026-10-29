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

using System.Text;

namespace Listenarr.Application.Search.Matching;

public sealed record ReleaseTitleMatch(bool Complete, bool Credited, string? Title, bool FoundTitle)
{
    public ReleaseInterpretation? Interpretation { get; init; }
}

/// <summary>Bind complete title and credit spans, then account for every surrounding field.</summary>
public static class ReleaseNameInterpreter
{
    private sealed record WorkTitle(string Display, string Main, string? Subtitle = null);
    public static ReleaseTitleMatch Match(string release, BookMatchContext context,
        IReadOnlyList<string> suppliedAuthors) => Match(release, context, suppliedAuthors, CancellationToken.None);

    public static ReleaseTitleMatch Match(string release, BookMatchContext context,
        IReadOnlyList<string> suppliedAuthors, CancellationToken cancellationToken)
    {
        var result = Interpret(release, context, suppliedAuthors, cancellationToken);
        return new(result.Complete, result.Credited, result.Title, result.FoundTitle) { Interpretation = result };
    }

    public static ReleaseInterpretation Interpret(string release, BookMatchContext context,
        IReadOnlyList<string> suppliedAuthors, CancellationToken cancellationToken = default)
    {
        var budget = new ReleaseAssessmentBudget(cancellationToken);
        if (release.Length > 16384 || context.Titles.Count > 64 || context.Authors.Count > 64
            || context.Narrators.Count > 64 || context.SeriesReferences.Count > 64 || suppliedAuthors.Count > 64)
            return new(false, false, null, false, [], "assessment_budget_exceeded");
        var scene = ReleaseTextView.LooksLikeScene(release);
        var found = false;
        var attempts = 0;
        string? reason = null;
        IReadOnlyList<ReleaseSpan> partial = [];
        ReleaseInterpretation? uncredited = null;
        var titles = context.Titles.Select(t => new WorkTitle(t, t)).ToList();
        if (context.TitleParts is { } parts)
            titles.Insert(0, new(parts.MainTitle + ": " + parts.Subtitle, parts.MainTitle, parts.Subtitle));
        var views = new Dictionary<int, ReleaseTextView>();
        var seenAnchors = new HashSet<(int Mode, int Start, int Length)>();
        var seenSpans = new HashSet<(int Mode, int Start, int End)>();
        var protectedMainSpans = new HashSet<(int Start, int Length)>();
        // Resolve reference-backed subtitle roles across supported views before
        // allowing a shorter literal title to treat those same words as a credit.
        foreach (var title in titles)
        {
            foreach (var mode in new[] { 0, 1, 2 })
            {
                if (mode == 2 && !scene) continue;
                // A scene view cannot remove meaningful literal-title periods.
                if (mode == 2 && (title.Main.Contains('.') || title.Subtitle?.Contains('.') == true)) continue;
                if (!views.TryGetValue(mode, out var view))
                    views.Add(mode, view = ReleaseTextView.Create(release, mode > 0, mode == 2, budget));
                if (budget.Exhausted) break;
                var interpreter = new ReleaseSpanInterpreter(view, context, suppliedAuthors, budget);
                var needle = mode > 0 ? IdentityText.Presentation(title.Main) : title.Main;
                var subtitle = title.Subtitle is null ? null : mode > 0
                    ? IdentityText.Presentation(title.Subtitle) : title.Subtitle;
                for (var offset = 0; offset <= view.Text.Length - needle.Length && budget.Spend(view.Text.Length - offset);)
                {
                    var index = view.Text.IndexOf(needle, offset, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    offset = index + Math.Max(1, needle.Length);
                    if (seenAnchors.Add((mode, index, needle.Length)) && ++attempts > 32)
                        return new(false, false, null, found, [], "assessment_budget_exceeded");
                    var mainEnd = index + needle.Length;
                    if (!Boundary(view.Text, index - 1) || !Boundary(view.Text, mainEnd)) continue;
                    if (InExplicitNarrator(view.Text, index)) continue;
                    found = true;
                    var end = mainEnd;
                    var subtitleStart = mainEnd;
                    var rawMain = view.Span(index, needle.Length, ReleaseSpanRole.PrimaryTitle);
                    if (subtitle != null)
                    {
                        if (!BindSubtitle(view.Text, mainEnd, subtitle, budget, out subtitleStart, out end)) continue;
                        // A stored subtitle cannot be reused as a credit by the shorter
                        // main-title alternative, even if it happens to be a known name.
                        protectedMainSpans.Add((rawMain.Start, rawMain.Length));
                    }
                    else if (protectedMainSpans.Contains((rawMain.Start, rawMain.Length))) continue;
                    if (!seenSpans.Add((mode, index, end))) continue;
                    var left = interpreter.Explain(0, index, true);
                    var right = interpreter.Explain(end, view.Text.Length, false);
                    if (budget.Exhausted) break;
                    reason ??= left.Reason ?? right.Reason;
                    var spans = new List<ReleaseSpan> { view.Span(index, needle.Length, ReleaseSpanRole.PrimaryTitle) };
                    if (subtitle != null) spans.Add(view.Span(subtitleStart, end - subtitleStart, ReleaseSpanRole.ReferenceSubtitle));
                    spans.AddRange(left.Spans);
                    spans.AddRange(right.Spans);
                    if (!left.Complete || !right.Complete)
                    {
                        if (partial.Count == 0 || spans.Where(s => s.Role == ReleaseSpanRole.Unknown).Sum(s => s.Length)
                            < partial.Where(s => s.Role == ReleaseSpanRole.Unknown).Sum(s => s.Length)) partial = spans.AsReadOnly();
                        continue;
                    }
                    var complete = new ReleaseInterpretation(true, left.Credited || right.Credited, title.Display, true,
                        spans.OrderBy(s => s.Start).ThenBy(s => s.Role).ToList().AsReadOnly());
                    if (complete.Credited) return complete;
                    uncredited ??= complete;
                }
            }
            if (budget.Exhausted) break;
        }
        if (!budget.Exhausted && uncredited != null) return uncredited;
        return new(false, false, null, found, partial,
            budget.Exhausted ? "assessment_budget_exceeded" : reason);
    }

    private static bool BindSubtitle(string text, int mainEnd, string subtitle,
        ReleaseAssessmentBudget budget, out int start, out int end)
    {
        start = mainEnd;
        end = mainEnd;
        while (start < text.Length && (char.IsWhiteSpace(text[start]) || ":-‐‑‒–—―−".Contains(text[start])))
        {
            if (!budget.Spend()) return false;
            start++;
        }
        if (start == mainEnd || !budget.Spend(subtitle.Length)
            || !text.AsSpan(start).StartsWith(subtitle, StringComparison.OrdinalIgnoreCase)) return false;
        end = start + subtitle.Length;
        return Boundary(text, end);
    }

    private static bool InExplicitNarrator(string text, int at)
    {
        var boundary = at - 1;
        while (boundary >= 0 && !ReleaseAnnotationGrammar.Separator(text[boundary])) boundary--;
        var prefix = text[(boundary + 1)..at];
        return prefix.Contains("narrated by ", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("read by ", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("narrator ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Boundary(string value, int index)
    {
        if (index < 0 || index >= value.Length) return true;
        if (char.IsLowSurrogate(value[index]) && index > 0 && char.IsHighSurrogate(value[index - 1])) index--;
        return Rune.TryGetRuneAt(value, index, out var rune) && !Rune.IsLetterOrDigit(rune)
            && Rune.GetUnicodeCategory(rune) is not
                (System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark);
    }
}
