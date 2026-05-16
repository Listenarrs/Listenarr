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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search
{
    public class CompositeScoreResult
    {
        public double Total { get; set; }
        public Dictionary<string, double> Breakdown { get; set; } = new();
    }

    public static class CompositeScorer
    {
        public static CompositeScoreResult CalculateProwlarrStyleScore(SearchResult result, Indexer? indexer = null, ILogger? logger = null)
        {
            var res = new CompositeScoreResult();

            // Tier 1: Quality Score (0-1000 points)
            double qualityScore = GetQualityScore(result.Quality) * 1000.0;
            res.Breakdown["Quality"] = qualityScore;

            // Tier 2: Format Score (0-100 points)
            double formatScore = GetFormatScore(result.Format) * 100.0;
            res.Breakdown["Format"] = formatScore;

            // Tier 3: Indexer Priority inversion (1..50 -> 50..1) multiplied by 1000
            double indexerScore = 0;
            if (indexer != null)
            {
                var priority = Math.Clamp(indexer.Priority, 1, 50);
                indexerScore = (51 - priority) * 1000.0;
            }
            res.Breakdown["Indexer"] = indexerScore;

            // Tier 4: Seeds/Grabs (0-100) * 100
            double seedScore = CalculateSeedScore(result) * 100.0;
            res.Breakdown["Seed"] = seedScore;

            // Tier 5: Age (0-100) * 10
            DateTime publishedDate;
            double ageScore = 50.0; // default if unknown
            if (!string.IsNullOrEmpty(result.PublishedDate) && DateTime.TryParse(result.PublishedDate, out publishedDate))
            {
                ageScore = CalculateAgeScore(publishedDate) * 10.0;
            }
            res.Breakdown["Age"] = ageScore;

            // Tier 6: Size (0-100)
            double sizeScore = CalculateSizeScore(result.Size);
            res.Breakdown["Size"] = sizeScore;

            res.Total = res.Breakdown.Values.Sum();

            logger?.LogDebug("Composite scored '{Title}': Q={QScore}, F={FScore}, I={IScore}, S={SScore}, A={AScore}, Sz={SizeScore}, Total={Total}",
                result.Title, qualityScore, formatScore, indexerScore, seedScore, ageScore, sizeScore, res.Total);

            return res;
        }

        private static double CalculateSeedScore(SearchResult result)
        {
            var downloadType = (result.DownloadType ?? string.Empty).ToLower();

            if (downloadType.Contains("usenet") || downloadType.Contains("ddl") || !string.IsNullOrEmpty(result.NzbUrl))
            {
                var grabs = result.Grabs;
                if (grabs > 0)
                {
                    return Math.Min(100.0, 20.0 + (Math.Log10(grabs) * 20.0));
                }
                return 0.0;
            }

            // Torrent
            var seeders = result.Seeders ?? 0;
            if (seeders <= 0) return 0.0;

            var seederScore = Math.Min(100.0, 20.0 + (Math.Log10(seeders) * 20.0));
            var leechers = result.Leechers ?? 0;
            if (leechers > 0)
            {
                var ratio = (double)seeders / Math.Max(1, leechers);
                if (ratio > 2.0) seederScore += 10.0;
                else if (ratio > 1.0) seederScore += 5.0;
            }

            return Math.Min(100.0, seederScore);
        }

        private static double CalculateAgeScore(DateTime publishedDate)
        {
            if (publishedDate == DateTime.MinValue) return 50.0;
            var age = DateTime.UtcNow - publishedDate;
            if (age.TotalDays < 1) return 100.0;
            if (age.TotalDays < 7) return 90.0;
            if (age.TotalDays < 30) return 75.0;
            if (age.TotalDays < 90) return 60.0;
            if (age.TotalDays < 365) return 40.0;
            return 20.0;
        }

        private static double CalculateSizeScore(long sizeBytes)
        {
            if (sizeBytes <= 0) return 50.0;
            var sizeMB = sizeBytes / (1024.0 * 1024.0);
            if (sizeMB >= 100 && sizeMB <= 800) return 100.0;
            if (sizeMB >= 50 && sizeMB < 100) return 80.0;
            if (sizeMB > 800 && sizeMB <= 1500) return 80.0;
            if (sizeMB >= 10 && sizeMB < 50) return 50.0;
            if (sizeMB > 1500 && sizeMB <= 3000) return 50.0;
            if (sizeMB < 10) return 20.0;
            if (sizeMB > 3000) return 30.0;
            return 50.0;
        }

        private static int GetQualityScore(string? quality)
        {
            if (string.IsNullOrEmpty(quality))
                return 0;

            var lowerQuality = quality.ToLower();

            if (lowerQuality.Contains("flac"))
                return 100;

            if (lowerQuality.Contains("aax"))
                return 95;

            if (lowerQuality.Contains("m4b"))
                return 90;

            if (lowerQuality.Contains("opus"))
                return 85;

            if (ContainsVbrPreset(lowerQuality, "v0"))
                return 82;
            if (ContainsVbrPreset(lowerQuality, "v1"))
                return 76;
            if (ContainsVbrPreset(lowerQuality, "v2"))
                return 70;

            if (lowerQuality.Contains("aac") || lowerQuality.Contains("m4a"))
                return 78;

            if (lowerQuality.Contains("320"))
                return 80;
            if (lowerQuality.Contains("256"))
                return 74;
            if (lowerQuality.Contains("192"))
                return 60;

            if (lowerQuality.Contains("vbr") || lowerQuality.Contains("cbr"))
            {
                return 65;
            }

            if (lowerQuality.Contains("mp3") && !ContainsAnyBitrate(lowerQuality, "64", "128", "192", "256", "320"))
                return 65;

            if (lowerQuality.Contains("128"))
                return 50;
            if (lowerQuality.Contains("64"))
                return 40;

            return 0;
        }

        private static bool ContainsVbrPreset(string qualityLower, string preset)
        {
            return qualityLower.Contains(preset) ||
                   qualityLower.Contains($"-{preset}") ||
                   qualityLower.Contains($" {preset}");
        }

        private static bool ContainsAnyBitrate(string qualityLower, params string[] bitrates)
        {
            return bitrates.Any(b => qualityLower.Contains(b));
        }

        private static double GetFormatScore(string? format)
        {
            if (string.IsNullOrEmpty(format)) return 50.0;
            var fmt = format.ToLower();
            if (fmt.Contains("m4b")) return 100.0;
            if (fmt.Contains("flac")) return 95.0;
            if (fmt.Contains("opus")) return 90.0;
            if (fmt.Contains("m4a") || fmt.Contains("aac")) return 85.0;
            if (fmt.Contains("mp3")) return 75.0;
            if (fmt.Contains("ogg") || fmt.Contains("vorbis")) return 70.0;
            if (fmt.Contains("wma")) return 40.0;
            if (fmt.Contains("ra") || fmt.Contains("realaudio")) return 30.0;
            return 50.0;
        }
    }
}
