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
    // The relevance gate's own tests stop at Apply; these drive the whole sweep
    // (AutomaticSearchProcessor.RunCycleAsync) through to the download-service handoff, so a
    // wrong-author release that only shares a generic subtitle with the book ("The Shining: A
    // Novel" for "Dune: A Novel") is proven never to reach StartDownloadAsync on the automatic
    // sweep path, while the corroborated release is proven to reach it.
    [Trait("Name", "AutomaticSearchProcessorRelevanceTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class AutomaticSearchProcessorRelevanceTests : BaseTests
    {
        [Theory]
        [InlineData("Nausea", "Jean-Paul Sartre")]
        [InlineData("The Little Prince", "Antoine de Saint-Exupéry")]
        [InlineData("The Turn of the Screw", "James, Henry")]
        public async Task RunCycleAsync_CompleteAuthorSpanWithAnnotation_ReachesOnlyTheCorrectDownload(string title, string author)
        {
            var correct = Release(title + " - " + author + " [M4B]", artist: "");
            var search = SearchReturning(Release(title + " - Other Writer [M4B]", artist: "Other Writer"), correct);
            var download = new Mock<IDownloadService>();
            download.Setup(d => d.StartDownloadAsync(It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>())).ReturnsAsync("download-span");
            Init(builder => { builder.WithSingleton(search.Object); builder.WithSingleton(download.Object); builder.WithSingleton(AcceptingQualityService().Object); });
            var book = await SeedMonitoredAudiobookAsync(title, author);
            await RunCycleAsync();
            download.Verify(d => d.StartDownloadAsync(It.Is<SearchResult>(r => ReferenceEquals(r, correct)), DirectDownloadMetadataKeys.ClientId, book.Id), Times.Once);
            download.Verify(d => d.StartDownloadAsync(It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Once);
        }

        [Fact]
        public async Task RunCycleAsync_GenericSubtitle_WrongAuthorReleases_AreNotDownloaded()
        {
            var search = SearchReturning(
                Release("The Shining: A Novel", artist: "Stephen King"),
                Release("Dune", artist: "John Doe"));
            var download = new Mock<IDownloadService>(MockBehavior.Strict);
            Init(builder =>
            {
                builder.WithSingleton(search.Object);
                builder.WithSingleton(download.Object);
                builder.WithSingleton(AcceptingQualityService().Object);
            });
            await SeedMonitoredAudiobookAsync("Dune: A Novel", "Frank Herbert");

            await RunCycleAsync();

            // The gate drops both before scoring, so the download service is never asked to grab
            // either (the strict mock would throw on any call).
            download.Verify(d => d.StartDownloadAsync(
                It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        public async Task RunCycleAsync_GenericSubtitle_CorroboratedRelease_ReachesDownloadSelection()
        {
            var correct = Release("Dune", artist: "Frank Herbert");
            var search = SearchReturning(
                Release("The Shining: A Novel", artist: "Stephen King"),
                Release("Dune", artist: "John Doe"),
                correct);
            var download = new Mock<IDownloadService>();
            download.Setup(d => d.StartDownloadAsync(
                    It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()))
                .ReturnsAsync("download-1");
            Init(builder =>
            {
                builder.WithSingleton(search.Object);
                builder.WithSingleton(download.Object);
                builder.WithSingleton(AcceptingQualityService().Object);
            });
            var audiobook = await SeedMonitoredAudiobookAsync("Dune: A Novel", "Frank Herbert");

            await RunCycleAsync();

            download.Verify(d => d.StartDownloadAsync(
                    It.Is<SearchResult>(r => ReferenceEquals(r, correct)),
                    DirectDownloadMetadataKeys.ClientId,
                    audiobook.Id),
                Times.Once);
            download.Verify(d => d.StartDownloadAsync(
                    It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()),
                Times.Once);
        }

        private static SearchResult Release(string title, string artist) => new()
        {
            Title = title,
            Artist = artist,
            IdentityEvidence = new CandidateIdentityEvidence { ReleaseName = title, Authors = string.IsNullOrWhiteSpace(artist) ? [] : [new ContributorEvidence(artist, IdentityEvidenceOrigin.ProviderSupplied)] },
            DownloadType = DirectDownloadMetadataKeys.ClientId
        };

        private static Mock<ISearchService> SearchReturning(params SearchResult[] results)
        {
            var search = new Mock<ISearchService>();
            search.Setup(s => s.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    It.IsAny<bool>()))
                .ReturnsAsync(results.ToList());
            return search;
        }

        // Isolate the gate -> selection wiring from the quality scorer (its own tests cover scoring):
        // every result that survives the gate gets a positive, non-rejected score.
        private static Mock<IQualityProfileService> AcceptingQualityService()
        {
            var quality = new Mock<IQualityProfileService>();
            quality.Setup(q => q.ScoreSearchResults(
                    It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
                .ReturnsAsync((List<SearchResult> results, QualityProfile _) =>
                    results.Select(r => new QualityScore { SearchResult = r, TotalScore = 100 }).ToList());
            return quality;
        }

        private async Task<Audiobook> SeedMonitoredAudiobookAsync(string title, string author) =>
            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle(title)
                .WithSubtitle(title.Contains(": ", StringComparison.Ordinal) ? title[(title.IndexOf(": ", StringComparison.Ordinal) + 2)..] : null)
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
