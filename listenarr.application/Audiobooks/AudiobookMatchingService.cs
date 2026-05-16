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

using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Service for matching downloads with audiobooks using multiple identifying criteria
    /// </summary>
    public class AudiobookMatchingService : IAudiobookMatchingService
    {
        private readonly IAudiobookRepository _audiobooks;
        private readonly ILogger<AudiobookMatchingService> _logger;

        public AudiobookMatchingService(
            IAudiobookRepository audiobooks,
            ILogger<AudiobookMatchingService> logger)
        {
            _audiobooks = audiobooks;
            _logger = logger;
        }

        public async Task<Audiobook?> FindBestAudiobookMatchAsync(Download download, double minimumConfidence = 0.8)
        {
            var audiobooks = await _audiobooks.GetAllAsync();

            var bestMatch = audiobooks
                .Select(ab => new { Audiobook = ab, Confidence = CalculateMatchConfidence(download, ab) })
                .Where(match => match.Confidence >= minimumConfidence)
                .OrderByDescending(match => match.Confidence)
                .FirstOrDefault();

            if (bestMatch != null)
            {
                _logger.LogInformation("Found audiobook match for download '{Title}': {AudiobookTitle} (confidence: {Confidence:P1})",
                    download.Title, bestMatch.Audiobook.Title, bestMatch.Confidence);
            }

            return bestMatch?.Audiobook;
        }

        public async Task<Audiobook?> FindBestAudiobookMatchAsync(SearchResult searchResult, double minimumConfidence = 0.8)
        {
            var audiobooks = await _audiobooks.GetAllAsync();

            var bestMatch = audiobooks
                .Select(ab => new { Audiobook = ab, Confidence = CalculateMatchConfidence(searchResult, ab) })
                .Where(match => match.Confidence >= minimumConfidence)
                .OrderByDescending(match => match.Confidence)
                .FirstOrDefault();

            if (bestMatch != null)
            {
                _logger.LogInformation("Found audiobook match for search result '{Title}': {AudiobookTitle} (confidence: {Confidence:P1})",
                    searchResult.Title, bestMatch.Audiobook.Title, bestMatch.Confidence);
            }

            return bestMatch?.Audiobook;
        }

        public double CalculateMatchConfidence(Download download, Audiobook audiobook)
        {
            double confidence = 0.0;
            int criteriaCount = 0;

            // ASIN match (highest confidence - 40% weight)
            if (!string.IsNullOrEmpty(download.Asin) && !string.IsNullOrEmpty(audiobook.Asin))
            {
                if (string.Equals(download.Asin, audiobook.Asin, StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.4;
                }
                criteriaCount++;
            }

            // ISBN match (very high confidence - 35% weight)
            if (!string.IsNullOrWhiteSpace(download.Isbn) && audiobook.Isbn != null && audiobook.Isbn.Any())
            {
                if (audiobook.Isbn.Any(i => string.Equals(download.Isbn, i, StringComparison.OrdinalIgnoreCase)))
                {
                    confidence += 0.35;
                }
                criteriaCount++;
            }

            // Title match (15% weight)
            if (!string.IsNullOrEmpty(download.Title) && !string.IsNullOrEmpty(audiobook.Title))
            {
                confidence += CalculateTitleSimilarity(download.Title, audiobook.Title) * 0.15;
                criteriaCount++;
            }

            // Author/Artist match (10% weight)
            if (!string.IsNullOrEmpty(download.Artist) && audiobook.Authors?.Any() == true)
            {
                var authorMatch = audiobook.Authors.Any(author =>
                    string.Equals(download.Artist, author, StringComparison.OrdinalIgnoreCase) ||
                    download.Artist.Contains(author, StringComparison.OrdinalIgnoreCase) ||
                    author.Contains(download.Artist, StringComparison.OrdinalIgnoreCase));

                if (authorMatch)
                {
                    confidence += 0.1;
                }
                criteriaCount++;
            }

            // Series and series number match (combined 10% weight)
            if (!string.IsNullOrEmpty(download.Series) && !string.IsNullOrEmpty(audiobook.Series))
            {
                var seriesMatch = CalculateTitleSimilarity(download.Series, audiobook.Series);
                confidence += seriesMatch * 0.07;

                // Series number match (3% additional weight)
                if (!string.IsNullOrEmpty(download.SeriesNumber) && !string.IsNullOrEmpty(audiobook.SeriesNumber) &&
                    string.Equals(download.SeriesNumber, audiobook.SeriesNumber, StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.03;
                }
                criteriaCount++;
            }

            // Publisher match (5% weight)
            if (!string.IsNullOrEmpty(download.Publisher) && !string.IsNullOrEmpty(audiobook.Publisher))
            {
                if (string.Equals(download.Publisher, audiobook.Publisher, StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.05;
                }
                criteriaCount++;
            }

            // Runtime similarity (3% weight)
            if (download.Runtime.HasValue && audiobook.Runtime.HasValue)
            {
                var runtimeDiff = Math.Abs(download.Runtime.Value - audiobook.Runtime.Value);
                var runtimeSimilarity = Math.Max(0, 1.0 - (runtimeDiff / (double)Math.Max(download.Runtime.Value, audiobook.Runtime.Value)));
                confidence += runtimeSimilarity * 0.03;
                criteriaCount++;
            }

            // Language match (2% weight)
            if (!string.IsNullOrEmpty(download.Language) && !string.IsNullOrEmpty(audiobook.Language))
            {
                if (string.Equals(download.Language, audiobook.Language, StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.02;
                }
                criteriaCount++;
            }

            // If we have very few criteria to match on, reduce confidence
            if (criteriaCount < 3)
            {
                confidence *= 0.8; // Reduce confidence by 20% for insufficient data
            }

            return Math.Min(1.0, confidence);
        }

        public double CalculateMatchConfidence(SearchResult searchResult, Audiobook audiobook)
        {
            double confidence = 0.0;
            int criteriaCount = 0;

            // ASIN match (highest confidence - 40% weight)
            if (!string.IsNullOrEmpty(searchResult.Asin) && !string.IsNullOrEmpty(audiobook.Asin))
            {
                if (string.Equals(searchResult.Asin, audiobook.Asin, StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.4;
                }
                criteriaCount++;
            }

            // Title match (25% weight for search results since we don't have ISBN)
            if (!string.IsNullOrEmpty(searchResult.Title) && !string.IsNullOrEmpty(audiobook.Title))
            {
                confidence += CalculateTitleSimilarity(searchResult.Title, audiobook.Title) * 0.25;
                criteriaCount++;
            }

            // Author/Artist match (15% weight)
            if (!string.IsNullOrEmpty(searchResult.Artist) && audiobook.Authors?.Any() == true)
            {
                var authorMatch = audiobook.Authors.Any(author =>
                    string.Equals(searchResult.Artist, author, StringComparison.OrdinalIgnoreCase) ||
                    searchResult.Artist.Contains(author, StringComparison.OrdinalIgnoreCase) ||
                    author.Contains(searchResult.Artist, StringComparison.OrdinalIgnoreCase));

                if (authorMatch)
                {
                    confidence += 0.15;
                }
                criteriaCount++;
            }

            // Series and series number match (combined 10% weight)
            if (!string.IsNullOrEmpty(searchResult.Series) && !string.IsNullOrEmpty(audiobook.Series))
            {
                var seriesMatch = CalculateTitleSimilarity(searchResult.Series, audiobook.Series);
                confidence += seriesMatch * 0.07;

                if (!string.IsNullOrEmpty(searchResult.SeriesNumber) && !string.IsNullOrEmpty(audiobook.SeriesNumber) &&
                    string.Equals(searchResult.SeriesNumber, audiobook.SeriesNumber, StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.03;
                }
                criteriaCount++;
            }

            // Publisher match (5% weight)
            if (!string.IsNullOrEmpty(searchResult.Publisher) && !string.IsNullOrEmpty(audiobook.Publisher))
            {
                if (string.Equals(searchResult.Publisher, audiobook.Publisher, StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.05;
                }
                criteriaCount++;
            }

            // Runtime similarity (3% weight)
            if (searchResult.Runtime.HasValue && audiobook.Runtime.HasValue)
            {
                var runtimeDiff = Math.Abs(searchResult.Runtime.Value - audiobook.Runtime.Value);
                var runtimeSimilarity = Math.Max(0, 1.0 - (runtimeDiff / (double)Math.Max(searchResult.Runtime.Value, audiobook.Runtime.Value)));
                confidence += runtimeSimilarity * 0.03;
                criteriaCount++;
            }

            // Language match (2% weight)
            if (!string.IsNullOrEmpty(searchResult.Language) && !string.IsNullOrEmpty(audiobook.Language))
            {
                if (string.Equals(searchResult.Language, audiobook.Language, StringComparison.OrdinalIgnoreCase))
                {
                    confidence += 0.02;
                }
                criteriaCount++;
            }

            // If we have very few criteria to match on, reduce confidence
            if (criteriaCount < 3)
            {
                confidence *= 0.8;
            }

            return Math.Min(1.0, confidence);
        }

        public void PopulateDownloadFromSearchResult(Download download, SearchResult searchResult)
        {
            // Copy enhanced metadata from search result to download for better matching
            download.Title = searchResult.Title;
            download.Artist = searchResult.Artist;
            download.Album = searchResult.Album;
            download.Asin = searchResult.Asin;
            download.Series = searchResult.Series;
            download.SeriesNumber = searchResult.SeriesNumber;
            download.Publisher = searchResult.Publisher;
            download.Language = searchResult.Language;
            download.Runtime = searchResult.Runtime;
            download.ExpectedFileSize = searchResult.Size;
        }

        private double CalculateTitleSimilarity(string title1, string title2)
        {
            var normalized1 = TitleUtils.NormalizeTitle(title1);
            var normalized2 = TitleUtils.NormalizeTitle(title2);

            // Exact match
            if (string.Equals(normalized1, normalized2, StringComparison.OrdinalIgnoreCase))
                return 1.0;

            // Bidirectional contains
            if (normalized1.Contains(normalized2, StringComparison.OrdinalIgnoreCase) ||
                normalized2.Contains(normalized1, StringComparison.OrdinalIgnoreCase))
                return 0.8;

            // Partial word matching
            var words1 = normalized1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var words2 = normalized2.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var commonWords = words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Count();
            var totalWords = Math.Max(words1.Length, words2.Length);

            return totalWords > 0 ? (double)commonWords / totalWords : 0.0;
        }
    }
}
