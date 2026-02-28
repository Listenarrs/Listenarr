using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Listenarr.Api.Services.Scoring
{
    public class SearchResultScorer
    {
        private readonly ListenArrDbContext? _dbContext;
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

        public SearchResultScorer(ListenArrDbContext? dbContext, ILogger logger)
        {
            _dbContext = dbContext;
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
            foreach (var forbidden in profile.MustNotContain)
            {
                if (!string.IsNullOrEmpty(forbidden) && searchResult.Title.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    score.RejectionReasons.Add($"Contains forbidden word: '{forbidden}'");
                    score.TotalScore = -1;
                    return score;
                }
            }

            // Required words
            foreach (var required in profile.MustContain)
            {
                if (!string.IsNullOrEmpty(required) && !searchResult.Title.Contains(required, StringComparison.OrdinalIgnoreCase))
                {
                    score.RejectionReasons.Add($"Missing required word: '{required}'");
                    score.TotalScore = -1;
                    return score;
                }
            }

            // Detect NZB/Usenet more broadly
            var isNzb = !string.IsNullOrEmpty(searchResult.NzbUrl) ||
                         string.Equals(searchResult.DownloadType, "nzb", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(searchResult.DownloadType, "usenet", StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrEmpty(searchResult.IndexerImplementation) && (searchResult.IndexerImplementation.IndexOf("nzb", StringComparison.OrdinalIgnoreCase) >= 0 || searchResult.IndexerImplementation.IndexOf("usenet", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                         (!string.IsNullOrEmpty(searchResult.Source) && searchResult.Source.IndexOf("usenet", StringComparison.OrdinalIgnoreCase) >= 0) ||
                         (!string.IsNullOrEmpty(searchResult.ResultUrl) && (searchResult.ResultUrl.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase) || searchResult.ResultUrl.IndexOf("/nzb", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                         (!string.IsNullOrEmpty(searchResult.TorrentUrl) && searchResult.TorrentUrl.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase));

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
            if (searchResult.IndexerId.HasValue && _dbContext != null)
            {
                try
                {
                    var idx = await _dbContext.Indexers.FindAsync(searchResult.IndexerId.Value);
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
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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

                    var matched = false;
                    foreach (var f in profile.PreferredFormats)
                    {
                        if (string.IsNullOrWhiteSpace(f)) continue;
                        var token = f.ToLower().Trim();
                        if (fmtLower.Contains(token) || qualityLower.Contains(token) || urlLower.Contains("." + token) || urlLower.Contains(token) || titleLower.Contains(token))
                        {
                            matched = true;
                            score.ScoreBreakdown["FormatMatchedInFormat"] = 1;
                            score.TotalScore += 1;
                            break;
                        }
                    }
                    if (!matched)
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
                            foreach (var fmt in profile.PreferredFormats)
                            {
                                var f = (fmt ?? string.Empty).Trim().ToLower();
                                if (!string.IsNullOrEmpty(f) && !allowed.Contains(f)) allowed.Add(f);
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
                var bonus = 0;
                foreach (var w in profile.PreferredWords)
                {
                    if (!string.IsNullOrWhiteSpace(w) && (searchResult.Title ?? string.Empty).Contains(w, StringComparison.OrdinalIgnoreCase)) bonus += 5;
                }
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
            foreach (var f in preferredFormats)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                var token = f.ToLower().Trim();
                if (titleLower.Contains(token) || titleLower.Contains("[" + token + "]") || titleLower.Contains("(" + token + ")") || titleLower.Contains("." + token)) return token;
            }
            return null;
        }

        private static string? DetectLanguageFromTitle(string titleLower, List<string>? preferredLanguages)
        {
            if (preferredLanguages == null || preferredLanguages.Count == 0 || string.IsNullOrEmpty(titleLower)) return null;
            foreach (var lang in preferredLanguages)
            {
                if (string.IsNullOrWhiteSpace(lang)) continue;
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
    }
}
