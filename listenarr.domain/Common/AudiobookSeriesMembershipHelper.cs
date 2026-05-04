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
using Listenarr.Domain.Models;

namespace Listenarr.Domain.Common
{
    public static class AudiobookSeriesMembershipHelper
    {
        public static List<AudiobookSeriesMembership> Normalize(
            IEnumerable<AudiobookSeriesMembership>? memberships,
            string? legacySeries = null,
            string? legacySeriesNumber = null,
            string? legacySeriesAsin = null)
        {
            var normalized = new List<AudiobookSeriesMembership>();

            if (memberships != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var membership in memberships)
                {
                    var seriesName = NormalizeText(membership.SeriesName);
                    var seriesNumber = NormalizeText(membership.SeriesNumber);
                    var seriesAsin = NormalizeText(membership.SeriesAsin);

                    if (string.IsNullOrWhiteSpace(seriesName))
                    {
                        continue;
                    }

                    var dedupeKey = $"{seriesName}|{seriesNumber}|{seriesAsin}";
                    if (!seen.Add(dedupeKey))
                    {
                        continue;
                    }

                    normalized.Add(new AudiobookSeriesMembership
                    {
                        SeriesName = seriesName,
                        SeriesNumber = seriesNumber,
                        SeriesAsin = seriesAsin,
                        IsPrimary = membership.IsPrimary
                    });
                }
            }

            if (normalized.Count == 0)
            {
                var fallbackSeries = NormalizeText(legacySeries);
                if (!string.IsNullOrWhiteSpace(fallbackSeries))
                {
                    normalized.Add(new AudiobookSeriesMembership
                    {
                        SeriesName = fallbackSeries,
                        SeriesNumber = NormalizeText(legacySeriesNumber),
                        SeriesAsin = NormalizeText(legacySeriesAsin),
                        IsPrimary = true
                    });
                }
            }

            if (normalized.Count > 0)
            {
                var primaryIndex = normalized.FindIndex(m => m.IsPrimary);
                if (primaryIndex < 0)
                {
                    primaryIndex = 0;
                }

                for (var index = 0; index < normalized.Count; index++)
                {
                    normalized[index].IsPrimary = index == primaryIndex;
                    normalized[index].SortOrder = index;
                }
            }

            return normalized;
        }

        public static void ApplyToAudiobook(
            Audiobook audiobook,
            IEnumerable<AudiobookSeriesMembership>? memberships,
            string? legacySeries = null,
            string? legacySeriesNumber = null,
            string? legacySeriesAsin = null)
        {
            var normalized = Normalize(memberships, legacySeries, legacySeriesNumber, legacySeriesAsin);
            audiobook.SeriesMemberships = normalized.Count == 0 ? null : normalized;
            ApplyPrimarySeriesFields(audiobook);
        }

        public static void ApplyPrimarySeriesFields(Audiobook audiobook)
        {
            var primary = GetPrimaryMembership(audiobook.SeriesMemberships);
            audiobook.Series = primary?.SeriesName;
            audiobook.SeriesNumber = primary?.SeriesNumber;
        }

        public static AudiobookSeriesMembership? GetPrimaryMembership(IEnumerable<AudiobookSeriesMembership>? memberships)
        {
            if (memberships == null)
            {
                return null;
            }

            return memberships
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.SortOrder)
                .FirstOrDefault();
        }

        private static string? NormalizeText(string? value)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
