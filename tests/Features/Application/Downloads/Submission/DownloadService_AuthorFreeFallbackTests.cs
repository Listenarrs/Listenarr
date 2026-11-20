using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Submission
{
    /// <summary>
    /// The Search button path (<see cref="DownloadService.SearchAndDownloadAsync"/> with
    /// automatic scoring) retries once without the author, as the automatic sweep does (#801).
    /// </summary>
    [Trait("Name", "DownloadService_AuthorFreeFallbackTests")]
    [Trait("Category", "Downloads")]
    public class DownloadService_AuthorFreeFallbackTests : BaseTests
    {
        private const string Title = "A Princess of Mars";
        private const string Author = "Edgar Rice Burroughs";
        private const string FullQuery = Title + " " + Author;

        private Mock<ISearchService> _searchService = null!;
        private readonly List<PreparedDownloadSubmission> _submitted = new();

        public override Task InitializeAsync()
        {
            _searchService = new Mock<ISearchService>(MockBehavior.Strict);
            var gateway = new Mock<IDownloadClientGateway>();
            gateway
                .Setup(g => g.AddAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<PreparedDownloadSubmission>(), It.IsAny<CancellationToken>()))
                .Callback<DownloadClientConfiguration, PreparedDownloadSubmission, CancellationToken>((_, submission, _) => _submitted.Add(submission))
                .ReturnsAsync(new DownloadClientSubmissionResult("ABCDEF1234567890ABCDEF1234567890ABCDEF12"));
            _services.AddSingleton(_searchService.Object);
            _services.AddSingleton(gateway.Object);
            Init();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task EmptyFullQuery_RetriesTitleOnly_AndSubmitsTheFallbackRelease()
        {
            var book = await SeedAsync(Author);
            var release = Release(Title + " [M4B]");
            Returns(FullQuery, new List<SearchResult>());
            Returns(Title, new List<SearchResult> { release });

            var result = await SearchAndDownloadAsync(book);

            Assert.True(result.Success, result.Message);
            Assert.Equal(release.Title, Assert.Single(_submitted).Title);
            _searchService.Verify(s => s.SearchAsync(FullQuery, null, null, It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true), Times.Once);
            _searchService.Verify(s => s.SearchAsync(Title, null, null, It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true), Times.Once);
        }

        [Fact]
        public async Task FullQueryWithResults_SearchesWithTheAuthorFirst_AndDoesNotRetry()
        {
            var book = await SeedAsync(Author);
            Returns(FullQuery, new List<SearchResult> { Release(Title + " [M4B]", Author) });

            var result = await SearchAndDownloadAsync(book);

            Assert.True(result.Success, result.Message);
            _searchService.Verify(s => s.SearchAsync(FullQuery, null, null, It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true), Times.Once);
            _searchService.Verify(s => s.SearchAsync(Title, null, null, It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true), Times.Never);
        }

        [Fact]
        public async Task BothQueriesEmpty_RetriesExactlyOnce_AndReportsNoResults()
        {
            var book = await SeedAsync(Author);
            Returns(FullQuery, new List<SearchResult>());
            Returns(Title, new List<SearchResult>());

            var result = await SearchAndDownloadAsync(book);

            Assert.False(result.Success);
            Assert.Equal("No search results found", result.Message);
            _searchService.Verify(s => s.SearchAsync(It.IsAny<string>(), null, null, It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true), Times.Exactly(2));
        }

        [Fact]
        public async Task BookWithoutAuthor_DoesNotRepeatTheSameQuery()
        {
            var book = await SeedAsync();
            Returns(Title, new List<SearchResult>());

            var result = await SearchAndDownloadAsync(book);

            Assert.False(result.Success);
            _searchService.Verify(s => s.SearchAsync(Title, null, null, It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true), Times.Once);
        }

        // There is no catch-and-retry in DownloadService: an exception from the first search
        // propagates. This does not show that a real search cancels (SearchAsync takes no token).
        [Fact]
        public async Task FirstSearchThrows_PropagatesWithoutRetry()
        {
            var book = await SeedAsync(Author);
            _searchService
                .Setup(s => s.SearchAsync(FullQuery, null, null, It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true))
                .ThrowsAsync(new OperationCanceledException());

            await Assert.ThrowsAsync<OperationCanceledException>(() => SearchAndDownloadAsync(book));

            _searchService.Verify(s => s.SearchAsync(It.IsAny<string>(), null, null, It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true), Times.Once);
        }

        private void Returns(string query, List<SearchResult> results) =>
            _searchService
                .Setup(s => s.SearchAsync(query, null, null, It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), true))
                .ReturnsAsync(results);

        private Task<SearchAndDownloadResult> SearchAndDownloadAsync(Audiobook book) =>
            _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id);

        private async Task<Audiobook> SeedAsync(params string[] authors)
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(Path.GetTempPath())
                .Build());
            await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080,
                IsEnabled = true
            });
            var profile = await _qualityProfileRepository.AddAsync(
                new QualityProfileBuilder().WithName("Structured").WithStructuredDefaults().Build());
            var builder = new AudiobookBuilder()
                .WithBasePath(FileService.GetTempPath())
                .WithTitle(Title);
            foreach (var author in authors) builder.WithAuthor(author);
            var book = builder.Build();
            book.QualityProfileId = profile.Id;
            book.QualityProfile = null;
            book.Monitored = true;
            return await _audiobookRepository.AddAsync(book);
        }

        private static SearchResult Release(string title, string artist = Author) => new()
        {
            Title = title,
            Artist = artist,
            IdentityEvidence = new CandidateIdentityEvidence
            {
                ReleaseName = title,
                Authors = [new ContributorEvidence(artist, IdentityEvidenceOrigin.ProviderSupplied)]
            },
            DownloadType = "Torrent",
            Format = "M4B",
            Seeders = 50,
            MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12",
            Size = 400L * 1024 * 1024
        };
    }
}
