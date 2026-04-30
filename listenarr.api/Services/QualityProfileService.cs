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

namespace Listenarr.Api.Services
{
    public interface IQualityProfileService
    {
        Task<List<QualityProfile>> GetAllAsync();
        Task<QualityProfile?> GetByIdAsync(int id);
        Task<QualityProfile?> GetDefaultAsync();
        Task<QualityProfile> CreateAsync(QualityProfile profile);
        Task<QualityProfile> UpdateAsync(QualityProfile profile);
        Task<bool> DeleteAsync(int id);
        Task<QualityScore> ScoreSearchResult(SearchResult searchResult, QualityProfile profile);
        Task<List<QualityScore>> ScoreSearchResults(List<SearchResult> searchResults, QualityProfile profile);
    }

    public class QualityProfileService : IQualityProfileService
    {
        private readonly ILogger<QualityProfileService> _logger;
        private readonly IQualityProfileRepository _repository;
        private readonly IIndexerRepository? _indexerRepository;

        public QualityProfileService(IQualityProfileRepository repository, ILogger<QualityProfileService> logger, IIndexerRepository? indexerRepository = null)
        {
            _logger = logger;
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _indexerRepository = indexerRepository;
        }

        public async Task<List<QualityProfile>> GetAllAsync()
        {
            // Delegate persistence concerns to repository which encapsulates defensive JSON handling
            var profiles = await _repository.GetAllAsync();

            // Ensure default profiles have required qualities when listing
            foreach (var profile in profiles.Where(profile => profile.IsDefault))
            {
                await EnsureProfileHasRequiredQualitiesAsync(profile);
            }

            return profiles;
        }

        public async Task<QualityProfile?> GetByIdAsync(int id)
        {
            var profile = await _repository.FindByIdAsync(id);
            if (profile != null && profile.IsDefault)
            {
                await EnsureProfileHasRequiredQualitiesAsync(profile);
            }
            return profile;
        }

        public async Task<QualityProfile?> GetDefaultAsync()
        {
            var profile = await _repository.GetDefaultAsync();

            if (profile != null)
            {
                await EnsureProfileHasRequiredQualitiesAsync(profile);
            }
            return profile;
        }

        private async Task EnsureProfileHasRequiredQualitiesAsync(QualityProfile profile)
        {
            var requiredQualities = new Dictionary<string, int>
            {
                { "AAC 320kbps", 0 },
                { "AAC 256kbps", 1 },
                { "AAC 192kbps", 2 },
                { "AAC 128kbps", 3 },
                { "AAC 64kbps", 4 },
                { "MP3 320kbps", 5 },
                { "MP3 256kbps", 6 },
                { "MP3 VBR", 7 },
                { "MP3 192kbps", 8 },
                { "MP3 128kbps", 9 },
                { "MP3 64kbps", 10 }
            };

            var updated = false;
            foreach (var (qualityName, priority) in requiredQualities)
            {
                if (!profile.Qualities.Any(q => q.Quality == qualityName))
                {
                    // Add missing quality - all enabled by default
                    profile.Qualities.Add(new QualityDefinition
                    {
                        Quality = qualityName,
                        Allowed = true,
                        Priority = priority
                    });
                    updated = true;
                    _logger.LogInformation("Added missing quality '{Quality}' to profile '{ProfileName}'", qualityName, profile.Name);
                }
            }

            if (updated)
            {
                profile.UpdatedAt = DateTime.UtcNow;
                await _repository.UpdateAsync(profile);
                _logger.LogInformation("Updated quality profile '{ProfileName}' with missing qualities", profile.Name);
            }
        }

        public async Task<QualityProfile> CreateAsync(QualityProfile profile)
        {
            // If this is set as default, unset other defaults
            if (profile.IsDefault)
            {
                await UnsetAllDefaultsAsync();
            }

            profile.CreatedAt = DateTime.UtcNow;
            profile.UpdatedAt = DateTime.UtcNow;

            var created = await _repository.AddAsync(profile);
            _logger.LogInformation("Created quality profile: {Name} (ID: {Id})", created.Name, created.Id);
            return created;
        }

        public async Task<QualityProfile> UpdateAsync(QualityProfile profile)
        {
            var existing = await _repository.FindByIdAsync(profile.Id);
            if (existing == null)
            {
                throw new InvalidOperationException($"Quality profile with ID {profile.Id} not found");
            }

            // If this is set as default, unset other defaults
            if (profile.IsDefault && !existing.IsDefault)
            {
                await UnsetAllDefaultsAsync();
            }

            profile.UpdatedAt = DateTime.UtcNow;
            var updated = await _repository.UpdateAsync(profile);

            _logger.LogInformation("Updated quality profile: {Name} (ID: {Id})", updated.Name, updated.Id);
            return updated;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var profile = await _repository.FindByIdAsync(id);
            if (profile == null)
            {
                return false;
            }

            // Check if this is the default profile
            if (profile.IsDefault)
            {
                throw new InvalidOperationException(
                    "Cannot delete the default quality profile. Please set another profile as default first.");
            }

            // Check if any audiobooks are using this profile
            var audiobooksCount = await _repository.CountAudiobooksUsingProfileAsync(id);

            if (audiobooksCount > 0)
            {
                throw new InvalidOperationException(
                    $"Cannot delete quality profile '{profile.Name}' because it is used by {audiobooksCount} audiobook(s). " +
                    "Please reassign those audiobooks to a different profile first.");
            }

            var deleted = await _repository.DeleteAsync(id);
            if (deleted)
            {
                _logger.LogInformation("Deleted quality profile: {Name} (ID: {Id})", profile.Name, id);
            }
            return deleted;
        }

        private async Task UnsetAllDefaultsAsync()
        {
            var defaults = (await _repository.GetAllAsync()).Where(p => p.IsDefault).ToList();
            foreach (var profile in defaults)
            {
                profile.IsDefault = false;
                await _repository.UpdateAsync(profile);
            }
        }

        public async Task<QualityScore> ScoreSearchResult(SearchResult searchResult, QualityProfile profile)
        {
            var scorer = new Scoring.SearchResultScorer(_indexerRepository, _logger);
            var score = await scorer.Score(searchResult, profile);

            // Also calculate the Prowlarr-style composite (Smart) score so the UI
            // can display the same composite ranking details used for Smart sorting.
            try
            {
                var composite = Scoring.CompositeScorer.CalculateProwlarrStyleScore(searchResult, null, _logger);
                score.SmartScore = composite.Total;
                score.SmartScoreBreakdown = composite.Breakdown.ToDictionary(kv => kv.Key, kv => (int)Math.Round(kv.Value));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to compute composite smart score for search result {Id}", searchResult.Id);
            }

            return score;
        }


        // For NZB indexers we may be able to detect format and language from the title when not provided
        // If NZB and format is missing, attempt to detect a format token in the title





        // Manual search scoring logic for quality
        private int GetQualityScore(string? quality)
        {
            if (string.IsNullOrEmpty(quality))
                return 0;

            var lowerQuality = quality.ToLower();

            // Highest quality
            if (lowerQuality.Contains("flac"))
                return 100;

            // Audible format (AAX) - high quality
            if (lowerQuality.Contains("aax"))
                return 95;

            // Container formats
            if (lowerQuality.Contains("m4b"))
                return 90;

            // Modern efficient codecs
            if (lowerQuality.Contains("opus"))
                return 85;

            // VBR quality presets (LAME VBR presets like V0/V1/V2)
            if (ContainsVbrPreset(lowerQuality, "v0"))
                return 82;
            if (ContainsVbrPreset(lowerQuality, "v1"))
                return 76;
            if (ContainsVbrPreset(lowerQuality, "v2"))
                return 70;


            // AAC / M4A (check before numeric bitrates to prefer codec score for e.g. "AAC 256")
            if (lowerQuality.Contains("aac") || lowerQuality.Contains("m4a"))
                return 78;

            // Explicit numeric bitrates
            if (lowerQuality.Contains("320"))
                return 80;
            if (lowerQuality.Contains("256"))
                return 74;
            if (lowerQuality.Contains("192"))
                return 60;

            // VBR / CBR generic tokens (treat as mid-range if no numeric bitrate provided)
            if (lowerQuality.Contains("vbr") || lowerQuality.Contains("cbr"))
            {
                // If there's an explicit numeric bitrate elsewhere, that will have matched above.
                return 65;
            }

            // Generic MP3 mention without explicit bitrate -> mid-range
            if (lowerQuality.Contains("mp3") && !ContainsAnyBitrate(lowerQuality, "64", "128", "192", "256", "320"))
                return 65;

            if (lowerQuality.Contains("128"))
                return 50;
            if (lowerQuality.Contains("64"))
                return 40;

            return 0;
        }



        public async Task<List<QualityScore>> ScoreSearchResults(List<SearchResult> searchResults, QualityProfile profile)
        {
            var scores = await Task.WhenAll(searchResults.Select(result => ScoreSearchResult(result, profile)));

            // Ensure rejected results are ordered last regardless of numeric TotalScore
            return scores
                .OrderBy(s => s.IsRejected) // false (not rejected) first
                .ThenByDescending(s => s.TotalScore)
                .ToList();
        }

        /// <summary>
        /// Checks if a quality string contains VBR preset indicators (v0, v1, v2).
        /// </summary>
        private static bool ContainsVbrPreset(string qualityLower, string preset)
        {
            return qualityLower.Contains(preset) ||
                   qualityLower.Contains($"-{preset}") ||
                   qualityLower.Contains($" {preset}");
        }

        /// <summary>
        /// Checks if a quality string contains any of the specified bitrate indicators.
        /// </summary>
        private static bool ContainsAnyBitrate(string qualityLower, params string[] bitrates)
        {
            return bitrates.Any(b => qualityLower.Contains(b));
        }

        /// <summary>
        /// Determines if the profile has preferred languages configured.
        /// </summary>
        private static bool HasPreferredLanguages(QualityProfile profile)
        {
            return profile.PreferredLanguages != null && profile.PreferredLanguages.Count > 0;
        }

        /// <summary>
        /// Determines if the profile has preferred formats configured.
        /// </summary>
        private static bool HasPreferredFormats(QualityProfile profile)
        {
            return profile.PreferredFormats != null && profile.PreferredFormats.Count > 0;
        }

        /// <summary>
        /// Attempts to detect a preferred format token in a result title (case-insensitive).
        /// Returns the detected token (normalized) or null if nothing matched.
        /// </summary>
        private static string? DetectFormatFromTitle(string titleLower, List<string>? preferredFormats)
        {
            if (preferredFormats == null || preferredFormats.Count == 0 || string.IsNullOrEmpty(titleLower))
                return null;

            return preferredFormats
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.ToLower().Trim())
                // allow matching patterns like ".m4b", "[m4b]", "m4b" in the title
                .FirstOrDefault(token => titleLower.Contains(token) || titleLower.Contains("[" + token + "]") || titleLower.Contains("(" + token + ")") || titleLower.Contains("." + token));
        }

        /// <summary>
        /// Attempts to detect language from title using the profile's preferred languages as hints.
        /// Returns the detected language string (as provided in the profile) or null if none matched.
        /// </summary>
        private static string? DetectLanguageFromTitle(string titleLower, List<string>? preferredLanguages)
        {
            if (preferredLanguages == null || preferredLanguages.Count == 0 || string.IsNullOrEmpty(titleLower))
                return null;

            foreach (var lang in preferredLanguages.Where(lang => !string.IsNullOrWhiteSpace(lang)))
            {
                var token = lang.ToLower().Trim();

                // Basic match for language name or common short forms
                if (titleLower.Contains(token) || titleLower.Contains("[" + token + "]") || titleLower.Contains("(" + token + ")") || titleLower.Contains(" " + token + " "))
                {
                    return lang; // return original (possibly capitalised) profile language value
                }
            }

            // Also check a small set of common language tokens to detect language even when not present in profile
            var common = new Dictionary<string, string>
            {
                { "eng", "English" },
                { "english", "English" },
                { "es", "Spanish" },
                { "spanish", "Spanish" },
                { "de", "German" },
                { "german", "German" },
                { "fr", "French" },
                { "french", "French" }
            };

            foreach (var (token, name) in common)
            {
                if (titleLower.Contains(token)) return name;
            }

            return null;
        }
    }

}
