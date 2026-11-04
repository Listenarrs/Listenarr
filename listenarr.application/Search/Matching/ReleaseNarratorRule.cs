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

internal static class ReleaseNarratorRule
{
    private static readonly Regex Narrator = new(
        @"^(?:(?:narrated|read)\s+by|narrator\s*:)\s*(?<name>[\p{L}\p{M}][\p{L}\p{M}\s.'’,-]{0,199})$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool Match(string value) => (value.StartsWith("narrat", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("read ", StringComparison.OrdinalIgnoreCase)) && Narrator.IsMatch(value);

    public static ReleaseAnnotationResult Recognize(string value, ReadOnlySpan<char> tail, BookMatchContext context,
        bool scene, bool beforeTitle, bool technical, ReleaseAssessmentBudget budget)
    {
        if (Match(value)) return new(AnnotationOutcome.Explained, ReleaseSpanRole.NarratorCredit, "narrator.explicit");
        if (!scene || beforeTitle || !technical || tail.Length > 64 || value.Length == 0 || !value.All(char.IsLetter)) return default;
        // A partial narrator is supported only in the historical surname/year/group suffix.
        var fields = tail.Trim().ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || fields[0].Length != 4 || !fields[0].All(char.IsDigit) || !ReleaseGroupRule.Match(fields[1])) return default;
        string? known = null;
        foreach (var narrator in context.Narrators)
        {
            if (!budget.Spend(narrator.Length)) return default;
            if (!IdentityText.Equal(narrator[(narrator.LastIndexOf(' ') + 1)..], value)) continue;
            if (known != null) return new(AnnotationOutcome.Ambiguous, ReleaseSpanRole.NarratorCredit, Reason: "narrator_reference_ambiguous");
            known = narrator;
        }
        if (known == null) return default;
        foreach (var author in context.Authors)
        {
            if (!budget.Spend(author.Length)) return default;
            if (IdentityText.Equal(author[(author.LastIndexOf(' ') + 1)..], value)) return default;
        }
        return new(AnnotationOutcome.Explained, ReleaseSpanRole.NarratorCredit, "narrator.surname_year_group", known, "unique_surname");
    }
}
