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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Scoring
{
    public class SearchResultScorer
    {
        private readonly IIndexerRepository? _indexerRepository;
        private readonly ILogger _logger;

        // Configurable weights (tune as needed)
        public int BaseScore { get; set; } = 100;
        public int FormatMatchBonus { get; set; } = 5;
        public int FormatMissingPenalty { get; set; } = -8;
        public int QualityMissingPenalty { get; set; } = -10;
        public int LanguageMissingPenalty { get; set; } = -10;
        public int LanguageMismatchPenalty { get; set; } = -15;
        public int QualityNotAllowedPenalty { get; set; } = -20;
        public int ForbiddenWordRejectionFlag { get; set; } = -1; // sentinel for rejection

        public SearchResultScorer(IIndexerRepository? indexerRepository, ILogger logger)
        {
            _indexerRepository = indexerRepository;
            _logger = logger;
        }

        public async Task<QualityScore> Score(SearchResult searchResult, QualityProfile profile)
        {
            // Mirror existing QualityProfileService semantics, but organized and configurable
            var score = new QualityScore
            {
                SearchResult = searchResult,
                TotalScore = BaseScore,
                ScoreBreakdown = new Dictionary<string, int>(),
                RejectionReasons = new List<string>()
            };

            // Helper normalizers
            static string? NormalizeToken(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                var t = s.Trim();
                if (string.Equals(t, "unknown", StringComparison.OrdinalIgnoreCase)) return null;
                return t;
            }

            string? normalizedLanguage = NormalizeToken(searchResult.Language);
            string? normalizedFormat = NormalizeToken(searchResult.Format);
            string? normalizedQuality = NormalizeToken(searchResult.Quality);

            // Instant rejects: forbidden words
            var forbidden = profile.MustNotContain.FirstOrDefault(word =>
                !string.IsNullOrEmpty(word) &&
                searchResult.Title.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (forbidden != null)
            {
                score.RejectionReasons.Add($"Contains forbidden word: '{forbidden}'");
                score.TotalScore = -1;
                return score;
            }

            // Required words
            var missingRequired = profile.MustContain.FirstOrDefault(required =>
                !string.IsNullOrEmpty(required) &&
                !searchResult.Title.Contains(required, StringComparison.OrdinalIgnoreCase));
            if (missingRequired != null)
            {
                score.RejectionReasons.Add($"Missing required word: '{missingRequired}'");
                score.TotalScore = -1;
                return score;
            }

            // Detect NZB/Usenet more broadly
            var isNzb = IsNzbResult(searchResult);

            // Size checks (skip for NZB)
            if (!isNzb && searchResult.Size > 0)
            {
                if (profile.MinimumSize > 0 && searchResult.Size < profile.MinimumSize * 1024 * 1024)
                {
                    score.RejectionReasons.Add($"File too small (< {profile.MinimumSize} MB)");
                    score.TotalScore = -1;
                    return score;
                }
                if (profile.MaximumSize > 0 && searchResult.Size > profile.MaximumSize * 1024 * 1024)
                {
                    score.RejectionReasons.Add($"File too large (> {profile.MaximumSize} MB)");
                    score.TotalScore = -1;
                    return score;
                }
            }

            // Seeders requirement (treat null as 0)
            if (searchResult.DownloadType == "torrent" && (searchResult.Seeders ?? 0) < profile.MinimumSeeders)
            {
                var seedersValue = (searchResult.Seeders.HasValue) ? searchResult.Seeders.Value.ToString() : "(none)";
                score.RejectionReasons.Add($"Not enough seeders ({seedersValue} < {profile.MinimumSeeders})");
                score.TotalScore = -1;
                return score;
            }

            // Age checks and indexer retention
            double ageDays = 0;
            int indexerRetention = 0;
            if (searchResult.IndexerId.HasValue && _indexerRepository != null)
            {
                try
                {
                    var idx = await _indexerRepository.GetByIdAsync(searchResult.IndexerId.Value);
                    if (idx != null)
                    {
                        indexerRetention = idx.Retention;
                        if (!isNzb && !string.IsNullOrWhiteSpace(idx.Type) && string.Equals(idx.Type, "Usenet", StringComparison.OrdinalIgnoreCase))
                        {
                            isNzb = true;
                            _logger.LogDebug("Indexer {IndexerId} type '{Type}' detected as Usenet; applying NZB/Usenet exemptions", searchResult.IndexerId.Value, idx.Type);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to fetch indexer retention for IndexerId {Id}", searchResult.IndexerId.Value);
                }
            }

            if (!string.IsNullOrEmpty(searchResult.PublishedDate) && DateTime.TryParse(searchResult.PublishedDate, out var publishDate))
            {
                ageDays = (DateTime.UtcNow - publishDate).TotalDays;
                if (isNzb)
                {
                    if (indexerRetention > 0 && ageDays > indexerRetention)
                    {
                        score.RejectionReasons.Add($"Too old ({(int)ageDays} days > indexer retention {indexerRetention} days)");
                        score.TotalScore = -1;
                        return score;
                    }
                    if (profile.MaximumAge > 0 && ageDays > profile.MaximumAge)
                    {
                        score.RejectionReasons.Add($"Too old ({(int)ageDays} days > profile maximum age {profile.MaximumAge} days)");
                        score.TotalScore = -1;
                        return score;
                    }
                }
                else
                {
                    if (indexerRetention > 0)
                    {
                        if (ageDays > indexerRetention)
                        {
                            score.RejectionReasons.Add($"Too old ({(int)ageDays} days > indexer retention {indexerRetention} days)");
                            score.TotalScore = -1;
                            return score;
                        }
                    }
                    else if (profile.MaximumAge > 0 && ageDays > profile.MaximumAge)
                    {
                        score.RejectionReasons.Add($"Too old ({(int)ageDays} days > profile maximum age {profile.MaximumAge} days)");
                        score.TotalScore = -1;
                        return score;
                    }
                }
            }

            // Title lower for detection
            var titleLower = (searchResult.Title ?? string.Empty).ToLower();

            // Language detection for NZB
            if (isNzb && string.IsNullOrEmpty(normalizedLanguage) && HasPreferredLanguages(profile))
            {
                var detected = DetectLanguageFromTitle(titleLower, profile.PreferredLanguages);
                if (!string.IsNullOrEmpty(detected))
                {
                    normalizedLanguage = detected;
                    _logger.LogDebug("Detected language from title: {Language}", detected);
                }
            }

            // Language scoring
            if (HasPreferredLanguages(profile))
            {
                if (isNzb && string.IsNullOrEmpty(normalizedLanguage))
                {
                    _logger.LogDebug("NZB/Usenet missing language: no penalty applied for title '{Title}'", searchResult.Title);
                }
                else if (string.IsNullOrEmpty(normalizedLanguage))
                {
                    score.TotalScore += LanguageMissingPenalty;
                    score.ScoreBreakdown["Language"] = LanguageMissingPenalty;
                }
                else
                {
                    var matches = profile.PreferredLanguages.Any(l => normalizedLanguage.Equals(l, StringComparison.OrdinalIgnoreCase));
                    if (!matches)
                    {
                        score.TotalScore += LanguageMismatchPenalty;
                        score.ScoreBreakdown["LanguageMismatch"] = LanguageMismatchPenalty;
                    }
                }
            }

            // Format detection for NZB
            if (isNzb && string.IsNullOrEmpty(normalizedFormat) && HasPreferredFormats(profile))
            {
                var detected = DetectFormatFromTitle(titleLower, profile.PreferredFormats);
                if (!string.IsNullOrEmpty(detected))
                {
                    normalizedFormat = detected;
                    score.TotalScore += FormatMatchBonus;
                    score.ScoreBreakdown["FormatMatchedInTitle"] = FormatMatchBonus;
                }
            }

            // Format scoring
            if (HasPreferredFormats(profile))
            {
                if (isNzb && string.IsNullOrEmpty(normalizedFormat))
                {
                    _logger.LogDebug("NZB/Usenet missing format: no penalty applied for title '{Title}'", searchResult.Title);
                }
                else if (string.IsNullOrEmpty(normalizedFormat))
                {
                    score.TotalScore += FormatMissingPenalty;
                    score.ScoreBreakdown["Format"] = FormatMissingPenalty;
                }
                else
                {
                    var fmtLower = normalizedFormat.ToLower();
                    var qualityLower = (normalizedQuality ?? string.Empty).ToLower();
                    var urlLower = (searchResult.TorrentUrl ?? searchResult.Source ?? string.Empty).ToLower();

                    if (profile.PreferredFormats!
                        .Where(format => !string.IsNullOrWhiteSpace(format))
                        .Select(format => format.ToLower().Trim())
                        .Any(token => fmtLower.Contains(token) || qualityLower.Contains(token) || urlLower.Contains("." + token) || urlLower.Contains(token) || titleLower.Contains(token)))
                    {
                        score.ScoreBreakdown["FormatMatchedInFormat"] = 1;
                        score.TotalScore += 1;
                    }
                    else
                    {
                        score.TotalScore += -12;
                        score.ScoreBreakdown["FormatMismatch"] = -12;
                    }
                }
            }

            // Quality: missing -> penalty only when no format inferred and not NZB
            if (string.IsNullOrEmpty(normalizedQuality))
            {
                if (!isNzb)
                {
                    var formatDetected = !string.IsNullOrEmpty(normalizedFormat) || !string.IsNullOrEmpty(DetectFormatFromTitle(titleLower, profile.PreferredFormats)) || (!string.IsNullOrEmpty(searchResult.TorrentUrl) && (searchResult.TorrentUrl.ToLowerInvariant().Contains(".m4b") || searchResult.TorrentUrl.ToLowerInvariant().Contains(".mp3") || searchResult.TorrentUrl.ToLowerInvariant().Contains(".m4a")));
                    if (!formatDetected)
                    {
                        score.TotalScore += QualityMissingPenalty;
                        score.ScoreBreakdown["QualityMissing"] = QualityMissingPenalty;
                    }
                }
            }
            else
            {
                if (!isNzb)
                {
                    int qualityScore = GetQualityScore(normalizedQuality);
                    var qualityDeduction = 100 - qualityScore;
                    score.TotalScore -= qualityDeduction;
                    score.ScoreBreakdown["Quality"] = qualityScore;

                    if (profile.Qualities != null && profile.Qualities.Count > 0)
                    {
                        var allowed = profile.Qualities.Where(q => q.Allowed).Select(q => (q.Quality ?? string.Empty).ToLower()).ToList();
                        if (profile.PreferredFormats != null && profile.PreferredFormats.Count > 0)
                        {
                            foreach (var f in profile.PreferredFormats
                                .Where(format => !string.IsNullOrWhiteSpace(format))
                                .Select(format => format.Trim().ToLower())
                                .Where(format => !allowed.Contains(format)))
                            {
                                allowed.Add(f);
                            }
                        }

                        var detectedQualityLower = normalizedQuality.ToLower();
                        if (!allowed.Any(q => detectedQualityLower.Contains(q) || q.Contains(detectedQualityLower)))
                        {
                            score.TotalScore += QualityNotAllowedPenalty;
                            score.ScoreBreakdown["QualityNotAllowed"] = QualityNotAllowedPenalty;
                            score.RejectionReasons.Add($"Quality '{normalizedQuality}' not allowed by profile");
                        }
                    }
                }
            }

            // Preferred words bonus
            if (profile.PreferredWords != null && profile.PreferredWords.Count > 0)
            {
                var bonus = profile.PreferredWords
                    .Where(word => !string.IsNullOrWhiteSpace(word))
                    .Count(word => (searchResult.Title ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase)) * 5;
                if (bonus != 0)
                {
                    score.TotalScore += bonus;
                    score.ScoreBreakdown["PreferredWords"] = bonus;
                }
            }

            // Seeders bonus
            if ((searchResult.Seeders ?? 0) > 0)
            {
                var seedersBonus = Math.Min(10, searchResult.Seeders ?? 0);
                if (seedersBonus > 0)
                {
                    score.TotalScore += seedersBonus;
                    score.ScoreBreakdown["Seeders"] = seedersBonus;
                }
            }

            // Age penalty scaling up to -60 over 10 years
            if (ageDays > 0)
            {
                var agePenalty = (int)Math.Floor((ageDays / 3650.0) * 60.0);
                agePenalty = Math.Min(agePenalty, 60);
                if (agePenalty > 0)
                {
                    score.TotalScore -= agePenalty;
                    score.ScoreBreakdown["Age"] = -agePenalty;
                }
            }

            // Seeder-based offset for very old torrents
            if (!isNzb && ageDays >= 3650 && (searchResult.Seeders ?? 0) > 0)
            {
                var seeders = searchResult.Seeders ?? 0;
                var seedersAgeBonus = Math.Min(60, (int)Math.Floor((seeders / 20.0) * 60.0));
                if (seedersAgeBonus > 0)
                {
                    score.TotalScore += seedersAgeBonus;
                    score.ScoreBreakdown["SeedersAgeBonus"] = seedersAgeBonus;
                }
            }

            // Check minimum score threshold
            if (profile.MinimumScore > 0 && score.TotalScore < profile.MinimumScore)
            {
                score.RejectionReasons.Add($"Score {score.TotalScore} below profile minimum {profile.MinimumScore}");
                score.TotalScore = -1;
                return score;
            }

            // Final rejection check
            if (score.TotalScore <= 0)
            {
                score.RejectionReasons.Add("Computed score <= 0 (rejected)");
                return score;
            }

            if (!score.IsRejected)
            {
                score.TotalScore = Math.Clamp(score.TotalScore, 0, 100);
            }

            return score;
        }

        // Helpers (copied/adapted from old service)
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
