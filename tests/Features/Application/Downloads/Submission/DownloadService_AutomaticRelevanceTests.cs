using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Application.Search.Filters;

namespace Listenarr.Tests.Features.Application.Downloads.Submission
{
    /// <summary>
    /// End-to-end coverage for the automatic-search relevance gate on
    /// <see cref="DownloadService.SearchAndDownloadAsync"/> (#591): valid exact-match releases
    /// reach the download client, the off-topic release does not, and the metadata-page
    /// filter pipeline is never consulted on this path.
    /// </summary>
    [Trait("Name", "DownloadService_AutomaticRelevanceTests")]
    [Trait("Category", "Downloads")]
    public class DownloadService_AutomaticRelevanceTests : BaseTests
    {
        private Mock<ISearchService> _searchService = null!;
        private Mock<IDownloadClientGateway> _gateway = null!;
        private Mock<ISearchResultFilter> _metadataPageFilter = null!;
        private readonly List<PreparedDownloadSubmission> _submitted = new();

        public override Task InitializeAsync()
        {
            _searchService = new Mock<ISearchService>();
            _gateway = new Mock<IDownloadClientGateway>();
            _gateway
                .Setup(g => g.AddAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<PreparedDownloadSubmission>(), It.IsAny<CancellationToken>()))
                .Callback<DownloadClientConfiguration, PreparedDownloadSubmission, CancellationToken>((_, submission, _) => _submitted.Add(submission))
                .ReturnsAsync(new DownloadClientSubmissionResult("ABCDEF1234567890ABCDEF1234567890ABCDEF12"));

            // A strict filter in the pipeline: any call on the automatic path would throw.
            _metadataPageFilter = new Mock<ISearchResultFilter>(MockBehavior.Strict);

            _services.AddSingleton(_searchService.Object);
            _services.AddSingleton(_gateway.Object);
            _services.AddSingleton<ISearchResultFilter>(_metadataPageFilter.Object);
            _services.AddSingleton<ISearchResultFilter, ProductLikeTitleFilter>();
            _services.AddSingleton<ISearchResultFilter, PromotionalTitleFilter>();
            Init();
            return Task.CompletedTask;
        }

        private async Task<Audiobook> SeedAsync(string title, params string[] authors)
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
                .WithTitle(title);
            if (title.Contains(": ", StringComparison.Ordinal))
                builder.WithSubtitle(title[(title.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
            foreach (var author in authors) builder.WithAuthor(author);
            return await _audiobookRepository.AddAsync(Configure(builder.Build(), profile.Id));
        }

        private static Audiobook Configure(Audiobook book, int profileId)
        {
            book.QualityProfileId = profileId;
            book.QualityProfile = null;
            book.Monitored = true;
            return book;
        }

        private void IndexerReturns(params SearchResult[] results)
        {
            _searchService
                .Setup(s => s.SearchAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(), It.IsAny<SearchSortDirection>(), It.IsAny<bool>()))
                .ReturnsAsync(results.ToList());
        }

        private static SearchResult Release(string title, string artist = "", int seeders = 50) => new()
        {
            Title = title,
            Artist = artist,
            IdentityEvidence = new CandidateIdentityEvidence { ReleaseName = title, Authors = string.IsNullOrWhiteSpace(artist) ? [] : [new ContributorEvidence(artist, IdentityEvidenceOrigin.ProviderSupplied)] },
            DownloadType = "Torrent",
            Format = "M4B",
            Seeders = seeders,
            MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12",
            Size = 400L * 1024 * 1024
        };

        [Fact]
        public async Task SearchAndDownloadAsync_UnverifiedContributorAlias_IsDeferred()
        {
            var book = await SeedAsync("Man's Search for Meaning", "Viktor E. Frankl");
            IndexerReturns(Release("Viktor Frankl - Man's Search for Meaning [M4B]"));
            var result = await _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id);
            Assert.False(result.Success);
            Assert.Empty(_submitted);
        }

        [Theory]
        // Review regressions: exact matches that the product-like / promotional metadata-page
        // filters reject on raw indexer titles ("ring", "search").
        [InlineData("The Lord of the Rings", "J. R. R. Tolkien", "J. R. R. Tolkien - The Lord of the Rings [M4B]", "")]
        // Provider that returns title and author separately (MyAnonamouse author_info).
        [InlineData("Dune", "Frank Herbert", "Dune", "Frank Herbert")]
        // Control release with the author in the title.
        [InlineData("Dune", "Frank Herbert", "Frank Herbert - Dune [M4B]", "")]
        public async Task SearchAndDownloadAsync_ExactMatch_IsSubmittedToClient(
            string audiobookTitle, string author, string resultTitle, string resultArtist)
        {
            var book = await SeedAsync(audiobookTitle, author);
            IndexerReturns(Release(resultTitle, resultArtist));

            var result = await _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id);

            Assert.True(result.Success, result.Message);
            var submission = Assert.Single(_submitted);
            Assert.Equal(resultTitle, submission.Title);
            _metadataPageFilter.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task SearchAndDownloadAsync_OffTopicRelease_IsNotSubmitted()
        {
            var book = await SeedAsync("Dog Diaries: A Middle School Story", "James Patterson");
            IndexerReturns(Release("Patterson Hood - Live at Largo (2019)"));

            var result = await _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id);

            Assert.False(result.Success);
            Assert.Equal("No acceptable search results found", result.Message);
            Assert.Empty(_submitted);
            _metadataPageFilter.VerifyNoOtherCalls();
        }

        [Theory]
        // Review regressions (#591, round 2): the correct release must be chosen over a better-seeded
        // decoy by the same author. Stored subtitles, native-script titles, NFC/NFD spellings and
        // apostrophe-omitted release names must not tip the choice the other way.
        [InlineData("David Copperfield: The Personal History, Adventures, Experience and Observation of David Copperfield the Younger",
            "Charles Dickens", "Charles Dickens - David Copperfield [M4B]", "Charles Dickens - Oliver Twist [M4B]")]
        [InlineData("\u0412\u043e\u0439\u043d\u0430 \u0438 \u043c\u0438\u0440", "\u041b\u0435\u0432 \u0422\u043e\u043b\u0441\u0442\u043e\u0439",
            "\u0412\u043e\u0439\u043d\u0430 \u0438 \u043c\u0438\u0440 [M4B]", "\u041b\u0435\u0432 \u0422\u043e\u043b\u0441\u0442\u043e\u0439 - \u0410\u043d\u043d\u0430 \u041a\u0430\u0440\u0435\u043d\u0438\u043d\u0430 [M4B]")]
        [InlineData("Les Mis\u00e9rables", "Victor Hugo", "Victor Hugo - Les Mise\u0301rables [M4B]", "Victor Hugo - Notre-Dame de Paris [M4B]")]
        [InlineData("Ender's Game", "Orson Scott Card", "Enders Game [M4B]", "Orson Scott Card - Speaker for the Dead [M4B]")]
        [InlineData("Good Omens: The Nice and Accurate Prophecies of Agnes Nutter, Witch", "Neil Gaiman", "Good Omens [M4B]", "Neil Gaiman - Neverwhere [M4B]")]
        public async Task SearchAndDownloadAsync_SameAuthorDecoy_SubmitsTheCorrectRelease(
            string audiobookTitle, string author, string correctRelease, string decoyRelease)
        {
            var book = await SeedAsync(audiobookTitle, author);
            IndexerReturns(Release(decoyRelease, seeders: 900), Release(correctRelease, artist: author, seeders: 5));

            var result = await _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id);

            Assert.True(result.Success, result.Message);
            var submission = Assert.Single(_submitted);
            Assert.Equal(correctRelease, submission.Title);
            _metadataPageFilter.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task SearchAndDownloadAsync_GenericTitle_OneOfSeveralCreditedAuthorsInArtist_IsSubmitted()
        {
            var book = await SeedAsync("Stories", "Neil Gaiman", "Al Sarrantonio", "Joe Hill", "Jodi Picoult", "Michael Moorcock");
            IndexerReturns(
                Release("Stories", artist: "Neil Gaiman", seeders: 5),
                Release("Joe Hill - Horns [M4B]", seeders: 900));

            var result = await _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id);

            Assert.True(result.Success, result.Message);
            var submission = Assert.Single(_submitted);
            Assert.Equal("Stories", submission.Title);
        }

        [Fact]
        public async Task SearchAndDownloadAsync_GenericSubtitle_SubmitsOnlyTheCorroboratedRelease()
        {
            // Review round 5 (#591): "Dune: A Novel" must not queue "The Shining: A Novel" by Stephen
            // King (shared subtitle only) nor an uncorroborated "Dune", however well they seed.
            var book = await SeedAsync("Dune: A Novel", "Frank Herbert");
            IndexerReturns(
                Release("The Shining: A Novel", artist: "Stephen King", seeders: 900),
                Release("Dune (Unabridged Edition)", artist: "John Doe", seeders: 500),
                Release("Dune", artist: "Frank Herbert", seeders: 5));

            var result = await _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id);

            Assert.True(result.Success, result.Message);
            var submission = Assert.Single(_submitted);
            Assert.Equal("Dune", submission.Title);
        }

        [Fact]
        public async Task SearchAndDownloadAsync_GenericSubtitle_OnlyWrongAuthorReleases_NothingIsSubmitted()
        {
            var book = await SeedAsync("Dune: A Novel", "Frank Herbert");
            IndexerReturns(
                Release("The Shining: A Novel", artist: "Stephen King", seeders: 900),
                Release("Dune", artist: "John Doe", seeders: 500));

            var result = await _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id);

            Assert.False(result.Success);
            Assert.Equal("No acceptable search results found", result.Message);
            Assert.Empty(_submitted);
        }

        [Fact]
        public async Task SearchAndDownloadAsync_MixedResults_SubmitsOnlyTheRelevantRelease()
        {
            var book = await SeedAsync("Dog Diaries: A Middle School Story", "James Patterson");
            IndexerReturns(
                Release("Patterson Hood - Live at Largo (2019)", seeders: 900),
                Release("James Patterson - Dog Diaries: A Middle School Story [M4B]", seeders: 5));

            var result = await _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id);

            Assert.True(result.Success, result.Message);
            var submission = Assert.Single(_submitted);
            Assert.Equal("James Patterson - Dog Diaries: A Middle School Story [M4B]", submission.Title);
        }
    }
}
