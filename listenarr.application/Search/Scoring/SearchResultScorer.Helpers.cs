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

namespace Listenarr.Application.Search.Scoring
{
    // The scoring helpers live here so SearchResultScorer.cs stays clear of the 500-line
    // architecture cap. They are pure title and profile inspection with no state of their own.
    public partial class SearchResultScorer
    {
        private static bool HasPreferredLanguages(QualityProfile profile) => profile.PreferredLanguages != null && profile.PreferredLanguages.Count > 0;
        private static bool HasPreferredFormats(QualityProfile profile) => profile.PreferredFormats != null && profile.PreferredFormats.Count > 0;

        private static string? DetectFormatFromTitle(string titleLower, List<string>? preferredFormats)
        {
            if (preferredFormats == null || preferredFormats.Count == 0 || string.IsNullOrEmpty(titleLower)) return null;
            return preferredFormats
                .Where(format => !string.IsNullOrWhiteSpace(format))
                .Select(format => format.ToLower().Trim())
                .FirstOrDefault(token => titleLower.Contains(token) || titleLower.Contains("[" + token + "]") || titleLower.Contains("(" + token + ")") || titleLower.Contains("." + token));
        }

        private static string? DetectLanguageFromTitle(string titleLower, List<string>? preferredLanguages)
        {
            if (preferredLanguages == null || preferredLanguages.Count == 0 || string.IsNullOrEmpty(titleLower)) return null;
            foreach (var lang in preferredLanguages.Where(language => !string.IsNullOrWhiteSpace(language)))
            {
                var token = lang.ToLower().Trim();
                if (titleLower.Contains(token) || titleLower.Contains("[" + token + "]") || titleLower.Contains("(" + token + ")") || titleLower.Contains(" " + token + " "))
                {
                    return lang;
                }
            }
            var common = new Dictionary<string, string>
            {
                { "eng", "English" }, { "english", "English" }, { "es", "Spanish" }, { "spanish", "Spanish" },
                { "de", "German" }, { "german", "German" }, { "fr", "French" }, { "french", "French" }
            };
            foreach (var (token, name) in common) if (titleLower.Contains(token)) return name;
            return null;
        }

        private int GetQualityScore(string quality)
        {
            if (string.IsNullOrEmpty(quality)) return 0;
            var lowerQuality = quality.ToLower();
            if (lowerQuality.Contains("flac")) return 100;
            if (lowerQuality.Contains("aax")) return 95;
            if (lowerQuality.Contains("m4b")) return 90;
            if (lowerQuality.Contains("opus")) return 85;
            if (ContainsVbrPreset(lowerQuality, "v0")) return 82;
            if (ContainsVbrPreset(lowerQuality, "v1")) return 76;
            if (ContainsVbrPreset(lowerQuality, "v2")) return 70;
            if (lowerQuality.Contains("aac") || lowerQuality.Contains("m4a")) return 78;
            if (lowerQuality.Contains("320")) return 80;
            if (lowerQuality.Contains("256")) return 74;
            if (lowerQuality.Contains("192")) return 60;
            if (lowerQuality.Contains("vbr") || lowerQuality.Contains("cbr")) return 65;
            if (lowerQuality.Contains("mp3") && !ContainsAnyBitrate(lowerQuality, "64", "128", "192", "256", "320")) return 65;
            if (lowerQuality.Contains("128")) return 50;
            if (lowerQuality.Contains("64")) return 40;
            return 0;
        }

        private static bool ContainsVbrPreset(string qualityLower, string preset) => qualityLower.Contains(preset) || qualityLower.Contains($"-{preset}") || qualityLower.Contains($" {preset}");
        private static bool ContainsAnyBitrate(string qualityLower, params string[] bitrates) => bitrates.Any(b => qualityLower.Contains(b));

        private static bool IsNzbResult(SearchResult r)
        {
            bool hasNzbUrl = !string.IsNullOrEmpty(r.NzbUrl);
            bool isNzbType = string.Equals(r.DownloadType, "nzb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.DownloadType, "usenet", StringComparison.OrdinalIgnoreCase);
            bool indexerIndicatesNzb = !string.IsNullOrEmpty(r.IndexerImplementation)
                && (r.IndexerImplementation.IndexOf("nzb", StringComparison.OrdinalIgnoreCase) >= 0
                    || r.IndexerImplementation.IndexOf("usenet", StringComparison.OrdinalIgnoreCase) >= 0);
            bool sourceIndicatesNzb = !string.IsNullOrEmpty(r.Source)
                && r.Source.IndexOf("usenet", StringComparison.OrdinalIgnoreCase) >= 0;
            bool urlIndicatesNzb = !string.IsNullOrEmpty(r.ResultUrl)
                && (r.ResultUrl.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase)
                    || r.ResultUrl.IndexOf("/nzb", StringComparison.OrdinalIgnoreCase) >= 0);
            bool torrentIndicatesNzb = !string.IsNullOrEmpty(r.TorrentUrl)
                && r.TorrentUrl.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase);
            return hasNzbUrl || isNzbType || indexerIndicatesNzb || sourceIndicatesNzb || urlIndicatesNzb || torrentIndicatesNzb;
        }
    }
}
