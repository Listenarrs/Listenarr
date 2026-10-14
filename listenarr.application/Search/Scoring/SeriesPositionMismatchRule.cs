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

namespace Listenarr.Application.Search.Scoring
{
    /// <summary>
    /// Rejects releases that name another book of the same series. Books of a series often share
    /// their title ("Primal Hunter" for every volume on audible.fr), so the indexer query returns the
    /// whole series and the quality score alone cannot tell the volumes apart. A release is only
    /// rejected when its title carries an explicit position and none of the positions it covers is
    /// one of the audiobook's; releases without a position, omnibuses and ranges are left alone.
    /// </summary>
    public static class SeriesPositionMismatchRule
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

        // Volume keywords followed by a number: "T06", "Tome 6", "Book.6", "Vol. 6", "#6", "Livre 6", "Band 6".
        private const string Keyword = @"(?:t|tome|tomes|book|books|bk|vol|volume|volumes|livre|band|n[o°º]|#)";
        private const string Number = @"(\d{1,3}(?:[.,]5)?)(?![\d])";
        private const string RangeTail = @"(?:\s*(?:-|–|à|a|to|till|au|bis|&)\s*(?:" + Keyword + @"[\s._]*)?" + Number + ")?";

        private static readonly Regex KeywordPosition = new(
            @"(?<![\p{L}\d])" + Keyword + @"[\s._:]*" + Number + RangeTail, Options, Timeout);

        public static void Apply(Audiobook audiobook, IEnumerable<QualityScore> scores)
        {
            var wanted = GetWantedPositions(audiobook);
            if (wanted.Count == 0)
            {
                return;
            }

            var seriesNames = GetSeriesNames(audiobook);
            foreach (var score in scores)
            {
                var found = ParsePositions(score.SearchResult?.Title, seriesNames);
                if (found.Count == 0 || found.Any(range => wanted.Any(range.Contains)))
                {
                    continue;
                }

                score.RejectionReasons.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Release is book {0} of the series, not book {1}",
                    string.Join(", ", found.Select(range => range.ToString()).Distinct()),
                    string.Join(", ", wanted.Select(w => w.ToString(CultureInfo.InvariantCulture)))));
            }
        }

        internal static List<PositionRange> ParsePositions(string? title, IReadOnlyCollection<string> seriesNames)
        {
            var ranges = new List<PositionRange>();
            if (string.IsNullOrWhiteSpace(title))
            {
                return ranges;
            }

            foreach (Match match in KeywordPosition.Matches(title))
            {
                AddRange(ranges, match.Groups[1].Value, match.Groups[2].Value);
            }

            // "Primal Hunter 1 [2024]", "Primal.Hunter.04": a bare number right after the series name.
            foreach (var series in seriesNames)
            {
                var pattern = string.Join(@"[\s._\-:]+", series.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape))
                    + @"[\s._\-:,]+" + Number + RangeTail;
                foreach (Match match in Regex.Matches(title, @"(?<![\p{L}\d])" + pattern, Options, Timeout))
                {
                    AddRange(ranges, match.Groups[1].Value, match.Groups[2].Value);
                }
            }

            return ranges;
        }

        private static void AddRange(List<PositionRange> ranges, string start, string end)
        {
            if (!TryParsePosition(start, out var from))
            {
                return;
            }

            var to = TryParsePosition(end, out var parsedEnd) && parsedEnd >= from ? parsedEnd : from;
            ranges.Add(new PositionRange(from, to));
        }

        private static List<decimal> GetWantedPositions(Audiobook audiobook)
        {
            var numbers = new List<string?> { audiobook.SeriesNumber };
            numbers.AddRange(audiobook.SeriesMemberships?.Select(m => m.SeriesNumber) ?? Enumerable.Empty<string?>());

            return numbers
                .Select(n => TryParsePosition(n, out var value) ? value : (decimal?)null)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .Distinct()
                .ToList();
        }

        private static List<string> GetSeriesNames(Audiobook audiobook)
        {
            var names = new List<string?> { audiobook.Series };
            names.AddRange(audiobook.SeriesMemberships?.Select(m => m.SeriesName) ?? Enumerable.Empty<string?>());

            return names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryParsePosition(string? value, out decimal position)
        {
            position = 0;
            return !string.IsNullOrWhiteSpace(value)
                && decimal.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out position)
                && position > 0;
        }

        internal readonly record struct PositionRange(decimal From, decimal To)
        {
            public bool Contains(decimal position) => position >= From && position <= To;

            public override string ToString() => From == To
                ? From.ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{From}-{To}");
        }
    }
}
