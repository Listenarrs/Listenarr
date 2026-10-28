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
using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Matching;

internal static class ReleaseAnnotationGrammar
{
    private const string Token = @"(?:a novel|novels?|edition|unabridged|abridged|unabr|unb|m4b|m4a|mp3|flac|aac|opus|audiobook|audio book|audio|chapterized|variable|\d{2,4}\s*(?:kbps|kb/s|k)|\d{4})";
    private static readonly Regex Technical = new(
        @"^\.?" + Token + @"(?:[\s.!。]+" + Token + @")*[\s.!。]*$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Narrator = new(
        @"^(?:(?:narrated|read)\s+by|narrator\s*:)\s*(?<name>[\p{L}\p{M}][\p{L}\p{M}\s.'’,-]{0,199})$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NumberedSeries = new(
        @"^(?<name>.+?)(?:\s*[,#:]\s*|\s+)(?:(?:books?|bk|vol(?:ume)?|part)\s*)?(?<position>\d{1,4}(?:\.\d{1,3})?|[IVXLCDM]{1,12})$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PositionOnly = new(
        @"^(?:books?|bk|vol(?:ume)?|part)\s*#?\s*(?<position>\d{1,4}(?:\.\d{1,3})?|[IVXLCDM]{1,12})$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Groups = new(["NMR", "PoF", "BoB", "BBtv", "miok"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex SubtitleSeries = new(
        @"^(?<name>.+?),\s*(?:book|bk|volume|vol|part)\s+(?<position>\d{1,4}(?:\.\d{1,3})?|[IVXLCDM]{1,12})$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ReleaseSeriesReference? SeriesFromSubtitle(string subtitle)
    {
        var match = SubtitleSeries.Match(subtitle);
        return match.Success && TryPosition(match.Groups["position"].Value, out _)
            ? new(match.Groups["name"].Value.Trim(), match.Groups["position"].Value) : null;
    }

    public static bool IsTechnical(string value) => Technical.IsMatch(value);
    public static bool IsGroup(string value) => Groups.Contains(value);
    public static bool IsExplicitNarrator(string value) => Narrator.IsMatch(value);
    public static bool Separator(char value) => "[]();:,&-‐‑‒–—―−".Contains(value);

    public static (int Start, int Length)? PositionRange(string value)
    {
        var match = PositionOnly.Match(value);
        if (!match.Success) match = NumberedSeries.Match(value);
        return match.Success ? (match.Groups["position"].Index, match.Groups["position"].Length) : null;
    }

    public static bool PersonEqual(string known, string candidate, bool scene) =>
        IdentityText.PersonEqual(known, candidate)
        || scene && IdentityText.PersonEqual(ReleaseTextView.SceneWords(known), ReleaseTextView.SceneWords(candidate));

    public static bool Series(string value, BookMatchContext context, bool scene, bool beforeTitle,
        out string? conflict)
    {
        conflict = null;
        var explicitLabel = value.StartsWith("Series:", StringComparison.OrdinalIgnoreCase);
        if (explicitLabel) value = value[7..].Trim();
        string Compare(string name) => scene ? ReleaseTextView.SceneWords(name) : IdentityText.Presentation(name);
        var numbered = NumberedSeries.Match(value);
        var positionOnly = PositionOnly.Match(value);
        string? position = null;
        ReleaseSeriesReference[] known;
        if (positionOnly.Success)
        {
            position = positionOnly.Groups["position"].Value;
            known = context.SeriesReferences.ToArray();
            if (known.Select(s => Compare(s.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                return false;
        }
        else if (numbered.Success)
        {
            var name = numbered.Groups["name"].Value.Trim().TrimEnd(',');
            known = context.SeriesReferences.Where(s => IdentityText.Equal(Compare(s.Name), name)).ToArray();
            position = numbered.Groups["position"].Value;
        }
        else
        {
            if (!beforeTitle && !explicitLabel) return false;
            return context.SeriesReferences.Any(s => IdentityText.Equal(Compare(s.Name), value));
        }
        if (known.Length == 0) return false;
        if (!TryPosition(position, out var candidatePosition)) return false;
        var positions = known.Select(s => TryPosition(s.Position, out var p) ? (decimal?)p : null).Distinct().ToArray();
        if (positions.Length != 1 || positions[0] is null)
        {
            conflict = "reference_series_conflict";
            return false;
        }
        if (positions[0] != candidatePosition)
        {
            conflict = "series_position_conflict";
            return false;
        }
        return true;
    }

    public static bool TryPosition(string? value, out decimal position)
    {
        position = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16) return false;
        if (decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out position))
            return position >= 0;
        var roman = value.ToUpperInvariant();
        if (roman.Length > 12) return false;
        var values = new Dictionary<char, int> { ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100, ['D'] = 500, ['M'] = 1000 };
        var total = 0;
        for (var i = 0; i < roman.Length; i++)
        {
            if (!values.TryGetValue(roman[i], out var n)) return false;
            total += i + 1 < roman.Length && values.TryGetValue(roman[i + 1], out var next) && n < next ? -n : n;
        }
        if (total is < 1 or > 3999) return false;
        var remaining = total;
        var canonical = new System.Text.StringBuilder();
        foreach (var (number, text) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
            while (remaining >= number) { canonical.Append(text); remaining -= number; }
        if (canonical.ToString() != roman) return false;
        position = total;
        return true;
    }
}
