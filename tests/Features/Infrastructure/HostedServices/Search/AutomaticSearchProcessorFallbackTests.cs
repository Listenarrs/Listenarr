/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.HostedServices.Search
{
    // Full-cycle regression coverage for the author-free fallback (#1068/#1072). The classifier
    // unit tests stop at ConstrainFallbackResultsToTitle; these drive the whole automatic-search
    // cycle (AutomaticSearchProcessor.RunCycleAsync) through to the download-service handoff, so a
    // broadened-but-wrong result is proven never to reach StartDownloadAsync, while a correctly
    // attributed fallback is proven to reach it.
    [Trait("Name", "AutomaticSearchProcessorFallbackTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class AutomaticSearchProcessorFallbackTests : BaseTests
    {
        [Fact]
        public Task RunCycleAsync_AuthorFreeFallback_SameWordDifferentBook_IsNotDownloaded() =>
            AssertFallbackNotDownloadedAsync(
                title: "Artemis",
                author: "Andy Weir",
                fallbackResultTitle: "Eoin Colfer - Artemis Fowl - 2009");

        [Fact]
        public Task RunCycleAsync_AuthorFreeFallback_SameTitleDifferentAuthor_IsNotDownloaded() =>
            AssertFallbackNotDownloadedAsync(
                title: "Revival",
                author: "Stephen King",
                fallbackResultTitle: "Chris Weitz - Revival");

        [Fact]
        public async Task RunCycleAsync_AuthorFreeFallback_CorrectlyAttributed_ReachesDownloadSelection()
        {
            var fullQuery = "Artemis Andy Weir";
            var fallbackQuery = "Artemis";
            var correct = new SearchResult
            {
                Title = "Andy Weir - Artemis - 2017 - M4B",
                DownloadType = DirectDownloadMetadataKeys.ClientId
            };

            var search = new Mock<ISearchService>();
            SetupSearch(search, fullQuery, fallbackQuery, new List<SearchResult> { correct });
            var download = new Mock<IDownloadService>();
            download.Setup(d => d.StartDownloadAsync(
                    It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()))
                .ReturnsAsync("download-1");
            // Isolate the fallback -> selection wiring from the quality scorer (its own tests cover
            // scoring): accept the constrained result with a positive, non-rejected score.
            var quality = new Mock<IQualityProfileService>();
            quality.Setup(q => q.ScoreSearchResults(
                    It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
                .ReturnsAsync((List<SearchResult> results, QualityProfile _) =>
                    results.Select(r => new QualityScore { SearchResult = r, TotalScore = 100 }).ToList());
            Init(builder =>
            {
                builder.WithSingleton(search.Object);
                builder.WithSingleton(download.Object);
                builder.WithSingleton(quality.Object);
            });
            var audiobook = await SeedMonitoredAudiobookAsync("Artemis", "Andy Weir");

            await RunCycleAsync();

            download.Verify(d => d.StartDownloadAsync(
                    It.Is<SearchResult>(r => r.Title == correct.Title),
                    DirectDownloadMetadataKeys.ClientId,
                    audiobook.Id),
                Times.Once);
        }

        [Fact]
        public async Task RunCycleAsync_FullQueryResultsAllRejected_RetriesWithoutAuthorAndDownloads()
        {
            var fullQuery = "A Princess of Mars Edgar Rice Burroughs";
            var fallbackQuery = "A Princess of Mars";
            var junk = new SearchResult { Title = "Twenty Thousand Leagues Under the Sea - Jules Verne" };
            var correct = new SearchResult
            {
                Title = "Edgar Rice Burroughs - A Princess of Mars - M4B",
                DownloadType = DirectDownloadMetadataKeys.ClientId
            };

            var search = new Mock<ISearchService>();
            SetupSearch(search, fullQuery, fallbackQuery, new List<SearchResult> { correct });
            search.Setup(s => s.SearchAsync(
                    fullQuery,
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(new List<SearchResult> { junk });
            var download = new Mock<IDownloadService>();
            download.Setup(d => d.StartDownloadAsync(
                    It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()))
                .ReturnsAsync("download-1");
            var quality = new Mock<IQualityProfileService>();
            quality.Setup(q => q.ScoreSearchResults(
                    It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
                .ReturnsAsync((List<SearchResult> results, QualityProfile _) =>
                    results.Select(r => new QualityScore { SearchResult = r, TotalScore = 100 }).ToList());
            Init(builder =>
            {
                builder.WithSingleton(search.Object);
                builder.WithSingleton(download.Object);
                builder.WithSingleton(quality.Object);
            });
            var audiobook = await SeedMonitoredAudiobookAsync("A Princess of Mars", "Edgar Rice Burroughs");

            await RunCycleAsync();

            download.Verify(d => d.StartDownloadAsync(
                    It.Is<SearchResult>(r => r.Title == correct.Title),
                    DirectDownloadMetadataKeys.ClientId,
                    audiobook.Id),
                Times.Once);
            search.Verify(s => s.SearchAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), It.IsAny<bool>()),
                Times.Exactly(2));
        }

        [Fact]
        public async Task RunCycleAsync_FullQueryEmpty_FallbackOnlyRejected_RetriesExactlyOnce()
        {
            var search = new Mock<ISearchService>();
            SetupSearch(
                search,
                "A Princess of Mars Edgar Rice Burroughs",
                "A Princess of Mars",
                new List<SearchResult> { new() { Title = "Twenty Thousand Leagues Under the Sea - Jules Verne" } });
            var download = new Mock<IDownloadService>(MockBehavior.Strict);
            Init(builder =>
            {
                builder.WithSingleton(search.Object);
                builder.WithSingleton(download.Object);
            });
            await SeedMonitoredAudiobookAsync("A Princess of Mars", "Edgar Rice Burroughs");

            await RunCycleAsync();

            search.Verify(s => s.SearchAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), It.IsAny<bool>()),
                Times.Exactly(2));
            download.Verify(d => d.StartDownloadAsync(
                It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        public async Task RunCycleAsync_NoAuthor_FullAndTitleOnlyQueriesIdentical_OnlyRejected_SearchesOnce()
        {
            var search = new Mock<ISearchService>();
            search.Setup(s => s.SearchAsync(
                    "A Princess of Mars",
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(new List<SearchResult> { new() { Title = "Twenty Thousand Leagues Under the Sea - Jules Verne" } });
            var download = new Mock<IDownloadService>(MockBehavior.Strict);
            Init(builder =>
            {
                builder.WithSingleton(search.Object);
                builder.WithSingleton(download.Object);
            });
            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A Princess of Mars")
                .WithSeries("Barsoom")
                .WithMonitored()
                .WithQualityProfile(new QualityProfile { Name = "Any" })
                .Build());

            await RunCycleAsync();

            // With no author the title-only query equals the full query, so repeating it would only
            // spend a second request on the same indexers.
            search.Verify(s => s.SearchAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), It.IsAny<bool>()),
                Times.Once);
            download.Verify(d => d.StartDownloadAsync(
                It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        public async Task RunCycleAsync_RetryAfterRejectedResults_DoesNotGrabABlockedRelease()
        {
            var fullQuery = "A Princess of Mars Edgar Rice Burroughs";
            var fallbackQuery = "A Princess of Mars";
            var junk = new SearchResult { Title = "Twenty Thousand Leagues Under the Sea - Jules Verne" };
            var blockedRelease = new SearchResult
            {
                Title = "Edgar Rice Burroughs - A Princess of Mars - M4B",
                DownloadType = DirectDownloadMetadataKeys.ClientId,
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12"
            };

            var search = new Mock<ISearchService>();
            SetupSearch(search, fullQuery, fallbackQuery, new List<SearchResult> { blockedRelease });
            search.Setup(s => s.SearchAsync(
                    fullQuery,
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(new List<SearchResult> { junk });
            var identifier = ReleaseIdentity.For(blockedRelease);
            Assert.NotNull(identifier);
            var blocklist = new Mock<IBlocklistService>();
            blocklist.Setup(b => b.GetBlockedIdentifiersAsync(It.IsAny<int>()))
                .ReturnsAsync([identifier]);
            var download = new Mock<IDownloadService>(MockBehavior.Strict);
            var quality = new Mock<IQualityProfileService>();
            quality.Setup(q => q.ScoreSearchResults(
                    It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
                .ReturnsAsync((List<SearchResult> results, QualityProfile _) =>
                    results.Select(r => new QualityScore { SearchResult = r, TotalScore = 100 }).ToList());
            Init(builder =>
            {
                builder.WithSingleton(search.Object);
                builder.WithSingleton(download.Object);
                builder.WithSingleton(blocklist.Object);
                builder.WithSingleton(quality.Object);
            });
            await SeedMonitoredAudiobookAsync("A Princess of Mars", "Edgar Rice Burroughs");

            await RunCycleAsync();

            download.Verify(d => d.StartDownloadAsync(
                It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
        }

        private async Task AssertFallbackNotDownloadedAsync(
            string title,
            string author,
            string fallbackResultTitle)
        {
            var fullQuery = $"{title} {author}";
            var fallbackQuery = title;
            var search = new Mock<ISearchService>();
            SetupSearch(
                search,
                fullQuery,
                fallbackQuery,
                new List<SearchResult> { new() { Title = fallbackResultTitle } });
            var download = new Mock<IDownloadService>(MockBehavior.Strict);
            Init(builder =>
            {
                builder.WithSingleton(search.Object);
                builder.WithSingleton(download.Object);
            });
            await SeedMonitoredAudiobookAsync(title, author);

            await RunCycleAsync();

            // The guard drops the wrong book before scoring, so the download service is never asked
            // to grab it (strict mock would throw on any call).
            download.Verify(d => d.StartDownloadAsync(
                It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
        }

        private static void SetupSearch(
            Mock<ISearchService> search,
            string fullQuery,
            string fallbackQuery,
            List<SearchResult> fallbackResults)
        {
            search.Setup(s => s.SearchAsync(
                    fullQuery,
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(new List<SearchResult>());
            search.Setup(s => s.SearchAsync(
                    fallbackQuery,
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(fallbackResults);
        }

        private async Task<Audiobook> SeedMonitoredAudiobookAsync(string title, string author) =>
            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle(title)
                .WithAuthor(author)
                .WithMonitored()
                .WithQualityProfile(new QualityProfile { Name = "Any" })
                .Build());

        private async Task RunCycleAsync()
        {
            var processor = new AutomaticSearchProcessor(
                NullLogger<AutomaticSearchProcessor>.Instance,
                _provider.GetRequiredService<IServiceScopeFactory>());
            await processor.RunCycleAsync(CancellationToken.None);
        }
    }
}
