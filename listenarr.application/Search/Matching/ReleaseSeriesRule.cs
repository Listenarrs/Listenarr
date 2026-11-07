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

internal static class ReleaseSeriesRule
{
    private static readonly Regex NumberedSeries = new(
        @"^(?<name>.+?)(?:\s*[,#:.]\s*|\s+)(?:(?<cue>books?|bk|vol(?:ume)?|part)\s*)?(?<position>\d{1,4}(?:\.\d{1,3})?|[IVXLCDM]{1,12})$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PositionOnly = new(
        @"^(?:books?|bk|vol(?:ume)?|part)\s*#?\s*(?<position>\d{1,4}(?:\.\d{1,3})?|[IVXLCDM]{1,12})$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SubtitleSeries = new(
        @"^(?<name>.+?),\s*(?:book|bk|volume|vol|part)\s+(?<position>\d{1,4}(?:\.\d{1,3})?|[IVXLCDM]{1,12})$",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ReleaseSeriesReference? SeriesFromSubtitle(string subtitle)
    {
        var match = SubtitleSeries.Match(subtitle);
        return match.Success && TryPosition(match.Groups["position"].Value, out _)
            ? new(match.Groups["name"].Value.Trim(), match.Groups["position"].Value) : null;
    }

    public static (int Start, int Length)? PositionRange(string value)
    {
        var match = PositionOnly.Match(value);
        if (!match.Success) match = NumberedSeries.Match(value);
        return match.Success ? (match.Groups["position"].Index, match.Groups["position"].Length) : null;
    }

    public static ReleaseAnnotationResult Recognize(string value, ReleaseSeriesCatalog catalog, bool allowUnnumbered)
    {
        var explicitLabel = value.StartsWith("Series:", StringComparison.OrdinalIgnoreCase);
        if (explicitLabel) value = value[7..].Trim();
        var numbered = NumberedSeries.Match(value);
        var positionOnly = PositionOnly.Match(value);
        string? position = null;
        IReadOnlyList<ReleaseSeriesCatalog.Binding> known;
        var explicitPosition = positionOnly.Success || numbered.Success && (numbered.Groups["cue"].Success
            || value[..numbered.Groups["position"].Index].TrimEnd().EndsWith('#'));
        var literal = allowUnnumbered && !explicitPosition ? catalog.ResolveExact(value) : [];
        if (literal.Count > 0 && !positionOnly.Success)
            known = literal;
        else if (positionOnly.Success)
        {
            position = positionOnly.Groups["position"].Value;
            if (catalog.Facts.Count != 1) return default;
            known = [new(catalog.Facts[0], false)];
        }
        else if (numbered.Success)
        {
            var name = numbered.Groups["name"].Value.Trim().TrimEnd(',');
            known = catalog.Resolve(name);
            position = numbered.Groups["position"].Value;
        }
        else
        {
            if (!allowUnnumbered && !explicitLabel) return default;
            known = catalog.Resolve(value);
        }
        if (known.Count == 0) return new(AnnotationOutcome.UnknownReference, ReleaseSpanRole.SeriesName);
        if (numbered.Success && !positionOnly.Success && position is { Length: 4 } && position.All(char.IsAsciiDigit)
            && !explicitPosition)
            return new(AnnotationOutcome.Ambiguous, ReleaseSpanRole.SeriesPosition, Reason: "numeric_annotation_ambiguous");
        if (known.Select(k => k.Fact).Distinct().Count() > 1)
            return new(AnnotationOutcome.Ambiguous, ReleaseSpanRole.SeriesName, Reason: "series_alias_ambiguous");
        var positions = known.SelectMany(k => k.Fact.Positions).Distinct().ToArray();
        if (known.Any(k => k.Fact.Invalid) || positions.Length > 1)
            return new(AnnotationOutcome.Ambiguous, ReleaseSpanRole.SeriesName, Reason: "reference_series_conflict");
        var strength = known.All(k => k.Alias) ? "alias" : "exact";
        IReadOnlyList<ReleaseSeriesClaim> claims = [];
        if (position != null)
        {
            if (!TryPosition(position, out var candidatePosition)) return default;
            if (positions.Length == 0)
            {
                if (positionOnly.Success) return new(AnnotationOutcome.UnknownReference, ReleaseSpanRole.SeriesPosition);
                strength = "unverified_position";
            }
            else if (positions[0] != candidatePosition)
                return new(AnnotationOutcome.Conflict, ReleaseSpanRole.SeriesPosition, Reason: "series_position_conflict");
            claims = known.Select(k => new ReleaseSeriesClaim(ReleaseSeriesCatalog.Key(k.Fact.Name), candidatePosition)).Distinct().ToArray();
        }
        return new(AnnotationOutcome.Explained, ReleaseSpanRole.SeriesName, "series.reference",
            string.Join("; ", known.Select(k => k.Fact.Name).Distinct().Order(StringComparer.Ordinal)), strength, PositionClaims: claims);
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
