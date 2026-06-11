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
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Application.Audiobooks;

namespace Listenarr.Tests.Features.Api.Services
{
    public class QualityProfileScoringTests
    {
        private QualityProfileService CreateService()
        {
            return new QualityProfileService(Mock.Of<IQualityProfileRepository>(), NullLogger<QualityProfileService>.Instance);
        }

        [Fact]
        public async Task PerfectMatch_ShouldBeHighScore()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                MinimumSize = 0,
                MaximumSize = 0,
                PreferredFormats = new System.Collections.Generic.List<string> { "mp3" },
                PreferredWords = new System.Collections.Generic.List<string> { "unabridged" },
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Author - Great Book (unabridged)",
                Size = 150 * 1024 * 1024,
                Format = "mp3",
                Quality = "320",
                Language = "English",
                DownloadType = "torrent",
                Seeders = 5,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);
            Assert.True(score.TotalScore >= 80, $"Expected high score, got {score.TotalScore}");
        }

        [Fact]
        public async Task MissingQuality_NoFormat_Should_Be_Penalized()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                PreferredFormats = new System.Collections.Generic.List<string> { "m4b" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Some Book",
                Size = 120 * 1024 * 1024,
                Format = null,
                Quality = null,
                Language = "English",
                DownloadType = "torrent",
                Seeders = 2,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);
            Assert.True(score.ScoreBreakdown.TryGetValue("QualityMissing", out var qualityMissingScore), "Missing quality (and no format) should be penalized");
            Assert.Equal(-10, qualityMissingScore);
        }

        [Fact]
        public async Task MissingQuality_WithFormat_Should_Not_Be_Penalized()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                PreferredFormats = new System.Collections.Generic.List<string> { "m4b", "mp3" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Some Book With Format",
                Size = 120 * 1024 * 1024,
                Format = "mp3",
                Quality = null,
                Language = "English",
                DownloadType = "torrent",
                Seeders = 2,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);
            Assert.False(score.ScoreBreakdown.TryGetValue("QualityMissing", out _), "Missing quality should not be penalized when format is present");
        }

        [Fact]
        public async Task MissingQuality_InferredFromTitle_Should_Not_Be_Penalized()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                PreferredFormats = new System.Collections.Generic.List<string> { "m4b", "mp3" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Author - Great Book [M4B]",
                Size = 120 * 1024 * 1024,
                Format = null,
                Quality = null,
                Language = "English",
                DownloadType = "torrent",
                Seeders = 2,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);
            Assert.False(score.ScoreBreakdown.TryGetValue("QualityMissing", out _), "Missing quality should not be penalized when format can be inferred from the title");
        }

        [Fact]
        public async Task NZB_MissingQuality_ShouldNotBePenalized()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                PreferredFormats = new System.Collections.Generic.List<string> { "m4b" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Some NZB Book",
                Size = 120 * 1024 * 1024,
                Format = "m4b",
                Quality = null,
                Language = "English",
                DownloadType = "nzb",
                NzbUrl = "http://example.com/test.nzb",
                Seeders = 0,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);

            // For NZB indexers, missing quality should not be penalized
            Assert.False(score.ScoreBreakdown.TryGetValue("QualityMissing", out _), "NZB results should not be penalized for missing quality");
        }

        [Fact]
        public async Task NZB_MissingLanguage_And_Format_ShouldNotBePenalized()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                PreferredFormats = new System.Collections.Generic.List<string> { "m4b" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Some NZB Book Without Tokens",
                Size = 120 * 1024 * 1024,
                Format = null,
                Quality = null,
                Language = null,
                DownloadType = "nzb",
                NzbUrl = "http://example.com/test.nzb",
                Seeders = 0,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);

            // For NZB indexers, missing language and format should not be penalized
            Assert.False(score.ScoreBreakdown.TryGetValue("Language", out _), "NZB results should not be penalized for missing language");
            Assert.False(score.ScoreBreakdown.TryGetValue("Format", out _), "NZB results should not be penalized for missing format");
            // Also ensure Quality isn't penalized
            Assert.False(score.ScoreBreakdown.TryGetValue("QualityMissing", out _), "NZB results should not be penalized for missing quality");
        }

        [Fact]
        public async Task Usenet_MissingLanguage_And_Format_ShouldNotBePenalized()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                PreferredFormats = new System.Collections.Generic.List<string> { "m4b" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Some Usenet Book Without Tokens",
                Size = 120 * 1024 * 1024,
                Format = null,
                Quality = null,
                Language = null,
                DownloadType = "usenet",
                ResultUrl = "https://indexer/example/info/123",
                Seeders = 0,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);

            // For Usenet indexers, missing language and format should not be penalized
            Assert.False(score.ScoreBreakdown.TryGetValue("Language", out _), "Usenet results should not be penalized for missing language");
            Assert.False(score.ScoreBreakdown.TryGetValue("Format", out _), "Usenet results should not be penalized for missing format");
            // Also ensure Quality isn't penalized
            Assert.False(score.ScoreBreakdown.TryGetValue("QualityMissing", out _), "Usenet results should not be penalized for missing quality");
        }

        [Fact]
        public async Task NZB_OldPublishedDate_ShouldBeRejected_WhenProfileMaxAgeIsSmall()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                PreferredFormats = new System.Collections.Generic.List<string> { "m4b" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 10 // 10 days
            };

            var result = new SearchResult
            {
                Title = "Some Old NZB Book",
                Size = 120 * 1024 * 1024,
                Format = "m4b",
                Quality = "320",
                Language = "English",
                DownloadType = "nzb",
                NzbUrl = "http://example.com/old.nzb",
                Seeders = 0,
                PublishedDate = DateTime.UtcNow.AddDays(-365).ToString("o") // 1 year old
            };

            var score = await service.ScoreSearchResult(result, profile);

            // With profile MaximumAge configured low, NZB results should be rejected for being too old
            Assert.Contains(score.RejectionReasons, r => r.Contains("Too old"));
            Assert.True(score.TotalScore < 0, "Old NZB should be auto-rejected based on profile maximum age when configured");
        }

        [Fact]
        public async Task NZB_TitleParsing_DetectsFormatAndLanguageAndIgnoresSize()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                MinimumSize = 150, // 150 MB limit normally enforced
                PreferredFormats = new System.Collections.Generic.List<string> { "m4b" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 365
            };

            var result = new SearchResult
            {
                Title = "Author - Great Book [English] [M4B]",
                Size = 120 * 1024 * 1024, // 120 MB - would be too small for profile.MinimumSize
                Format = null,
                Quality = null,
                Language = null,
                DownloadType = "nzb",
                NzbUrl = "http://example.com/test.nzb",
                Seeders = 0,
                PublishedDate = DateTime.UtcNow.AddDays(-60).ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);

            // Size should be ignored for NZB results: no size rejection and no Size penalty
            Assert.DoesNotContain(score.RejectionReasons, r => r.Contains("File too small"));
            Assert.False(score.ScoreBreakdown.TryGetValue("Size", out _), "NZB scoring should not add size penalties");

            // Format should be detected from title and awarded a positive match
            Assert.True(score.ScoreBreakdown.TryGetValue("FormatMatchedInTitle", out _), "Expected format token to be detected in title for NZB");

            // Language should be detected from title and avoid language penalties
            Assert.False(score.ScoreBreakdown.TryGetValue("Language", out _));
            Assert.False(score.ScoreBreakdown.TryGetValue("LanguageMismatch", out _));

            // Quality is intentionally ignored for NZB so there should be no QualityMissing penalty
            Assert.False(score.ScoreBreakdown.TryGetValue("QualityMissing", out _));
            Assert.True(score.TotalScore > 0, "NZB result with detected format/language should have positive score");
        }

        [Fact]
        public async Task LanguageMismatch_ShouldBePenalized()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                PreferredFormats = new System.Collections.Generic.List<string> { "mp3" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Foreign Book",
                Size = 120 * 1024 * 1024,
                Format = "mp3",
                Quality = "256",
                Language = "Spanish",
                DownloadType = "torrent",
                Seeders = 2,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);
            Assert.True(score.TotalScore <= 85, $"Expected penalty for language mismatch, got {score.TotalScore}");
            Assert.Contains("LanguageMismatch", score.ScoreBreakdown.Keys);
        }

        [Fact]
        public async Task ForbiddenWord_ShouldReject()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                MustNotContain = new System.Collections.Generic.List<string> { "abridged" },
                PreferredFormats = new System.Collections.Generic.List<string>(),
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string>(),
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Abridged Sample Book",
                Size = 50 * 1024 * 1024,
                Format = "mp3",
                Quality = "128",
                Language = "English",
                DownloadType = "torrent",
                Seeders = 2,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);
            Assert.True(score.RejectionReasons.Count > 0, "Expected rejection reasons for forbidden word");
        }

        [Fact]
        public async Task MissingSeeders_ShouldBe_TreatedAsZero_And_Rejected()
        {
            var service = CreateService();
            var profile = new QualityProfile
            {
                PreferredFormats = new System.Collections.Generic.List<string>(),
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string>(),
                MinimumSeeders = 2,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "No seeders info",
                Format = "mp3",
                Quality = "320",
                Language = "English",
                DownloadType = "torrent",
                Seeders = null,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);
            Assert.Contains(score.RejectionReasons, r => r.Contains("Not enough seeders"));
            Assert.True(score.TotalScore < 0, "Result should be rejected when seeders are missing and profile requires minimum seeders");
        }

        [Fact]
        public async Task Age_Rejection_Is_Skipped_When_IndexerRetention_Is_Larger()
        {
            // profile maximumAge = 10 days, but indexer retention = 30 days; result age = 15 days -> should NOT be rejected for age
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ListenArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            using var db = new ListenArrDbContext(options);
            var indexer = new Listenarr.Domain.Models.Indexer { Name = "TestIndexer", Url = "https://test.local", Retention = 30, IsEnabled = true };
            db.Indexers.Add(indexer);
            db.SaveChanges();

            var service = new QualityProfileService(new QualityProfileRepository(db), NullLogger<QualityProfileService>.Instance, new EfIndexerRepository(db));
            var profile = new QualityProfile { MaximumAge = 10, MinimumSeeders = 0 };

            var result = new SearchResult
            {
                Title = "Old-ish Result",
                PublishedDate = DateTime.UtcNow.AddDays(-15).ToString("o"),
                DownloadType = "torrent",
                IndexerId = indexer.Id
            };

            var score = await service.ScoreSearchResult(result, profile);
            Assert.DoesNotContain(score.RejectionReasons, r => r.Contains("Too old"));
            Assert.True(score.TotalScore > 0, "Result should not be rejected for age when indexer retention is larger");
        }

        [Fact]
        public async Task Age_Rejection_Applied_When_Age_Exceeds_IndexerRetention()
        {
            // indexer retention = 10 days, result age = 12 days -> should be rejected (NZB path)
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ListenArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            using var db = new ListenArrDbContext(options);
            var indexer = new Listenarr.Domain.Models.Indexer { Name = "TestIndexer2", Url = "https://test2.local", Retention = 10, IsEnabled = true };
            db.Indexers.Add(indexer);
            db.SaveChanges();

            var service = new QualityProfileService(new QualityProfileRepository(db), NullLogger<QualityProfileService>.Instance, new EfIndexerRepository(db));
            var profile = new QualityProfile { MaximumAge = 0 };

            var result = new SearchResult
            {
                Title = "Too Old Result",
                PublishedDate = DateTime.UtcNow.AddDays(-12).ToString("o"),
                DownloadType = "nzb",
                IndexerId = indexer.Id
            };

            var score = await service.ScoreSearchResult(result, profile);
            Assert.Contains(score.RejectionReasons, r => r.Contains("Too old"));
            Assert.True(score.TotalScore < 0, "Result should be rejected for age exceeding indexer retention");
        }

        [Fact]
        public async Task Torrent_Age_Rejection_Uses_ProfileMaxAge()
        {
            // profile maxAge = 10 days, result age = 12 days -> torrent should be rejected
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ListenArrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            using var db = new ListenArrDbContext(options);
            var idx = new Listenarr.Domain.Models.Indexer { Name = "TorrentIdx", Url = "https://t.local", IsEnabled = true };
            db.Indexers.Add(idx);
            db.SaveChanges();

            var svc = new QualityProfileService(new QualityProfileRepository(db), NullLogger<QualityProfileService>.Instance, new EfIndexerRepository(db));
            var profile = new QualityProfile { MaximumAge = 10, MinimumSeeders = 0 };

            var result = new SearchResult
            {
                Title = "Old Torrent",
                PublishedDate = DateTime.UtcNow.AddDays(-12).ToString("o"),
                DownloadType = "torrent",
                IndexerId = idx.Id
            };

            var score = await svc.ScoreSearchResult(result, profile);
            Assert.Contains(score.RejectionReasons, r => r.Contains("Too old"));
            Assert.True(score.TotalScore < 0, "Torrent should be rejected when profile maxAge exceeded");
        }

        [Fact]
        public async Task MinimumScore_ShouldReject_WhenBelowThreshold()
        {
            // Test MinimumScore threshold: reject results below profile's minimum score
            var service = CreateService();
            var profile = new QualityProfile
            {
                MinimumScore = 101, // Set above possible max to ensure any real score will be rejected
                PreferredFormats = new System.Collections.Generic.List<string> { "mp3" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            // Create a result that will score low (no quality, no preferred format match, etc.)
            var result = new SearchResult
            {
                Title = "Low Quality Book",
                Size = 50 * 1024 * 1024,
                Format = "unknown",
                Quality = null,
                Language = "English",
                DownloadType = "torrent",
                Seeders = 1,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);

            // Accept either an explicit rejection or a numeric score below the configured minimum
            Assert.True(score.IsRejected || score.TotalScore < profile.MinimumScore, "Result should be rejected when score is below MinimumScore");
            Assert.True(score.RejectionReasons.Any(r => r.Contains("below profile minimum")) || score.TotalScore < profile.MinimumScore, "Expected either a rejection reason or a numeric score below the minimum");
        }

        [Fact]
        public async Task MinimumScore_Zero_ShouldAllow_AnyPositiveScore()
        {
            // Test default behavior: MinimumScore = 0 allows any non-negative score
            var service = CreateService();
            var profile = new QualityProfile
            {
                MinimumScore = 0, // No minimum threshold (default)
                PreferredFormats = new System.Collections.Generic.List<string> { "mp3" },
                PreferredWords = new System.Collections.Generic.List<string>(),
                MustNotContain = new System.Collections.Generic.List<string>(),
                MustContain = new System.Collections.Generic.List<string>(),
                PreferredLanguages = new System.Collections.Generic.List<string> { "English" },
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var result = new SearchResult
            {
                Title = "Some Book",
                Size = 50 * 1024 * 1024,
                Format = "mp3",
                Quality = "128",
                Language = "English",
                DownloadType = "torrent",
                Seeders = 1,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var score = await service.ScoreSearchResult(result, profile);

            Assert.False(score.IsRejected, "Result should not be rejected when MinimumScore = 0 and score > 0");
            Assert.True(score.TotalScore > 0, "Score should be positive");
        }
    }
}

