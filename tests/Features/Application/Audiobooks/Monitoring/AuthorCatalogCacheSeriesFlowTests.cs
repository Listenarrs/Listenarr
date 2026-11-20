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
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Application.Audiobooks.Monitoring
{
    /// <summary>
    /// Drives the real author catalog service, its persisted cache, the real author monitoring service and the
    /// real audiobook repository over SQLite. Only the Audible fetch and the library add pipeline are faked, and
    /// the add stand-in saves the metadata through <c>ToAudiobook</c> exactly as the real add does.
    /// </summary>
    [Trait("Name", "AuthorCatalogCacheSeriesFlowTests")]
    [Trait("Category", "AuthorMonitoringService")]
    public class AuthorCatalogCacheSeriesFlowTests : BaseTests
    {
        private const string AuthorName = "H. Rider Haggard";
        private const string AuthorAsin = "AUTHORPLACEHOLDER";

        [Fact]
        public async Task MonitorAuthorAfterCatalogPreview_PersistsEverySeriesMembershipWithItsAsin()
        {
            await using var fixture = await CreateFixtureAsync();

            // The author page loads the catalog (and caches it) before the user clicks Monitor.
            var preview = await fixture.CatalogService.GetCatalogAsync(AuthorName, "us", 250);
            Assert.NotNull(preview);
            Assert.Equal(2, preview!.Books.Count);

            var cachedBooks = (await fixture.AudiobookRepository.GetCachedAuthorByNameAsync(AuthorName, "us"))?.CatalogBooks;
            Assert.Equal(2, cachedBooks?.Count);

            var result = await fixture.MonitoringService.MonitorAuthorAsync(new MonitorAuthorRequest
            {
                Name = AuthorName,
                Region = "us",
                Language = "english"
            });

            Assert.Equal(2, result.SyncResult.AddedCount);
            fixture.AudibleService.Verify(
                service => service.GetAllBooksByAuthorAsync(
                    AuthorName, AuthorAsin, It.IsAny<int>(), "us", It.IsAny<string?>()),
                Times.Once);

            await AssertPersistedMembershipsAsync(fixture);
        }

        [Fact]
        public async Task MonitorAuthorWithoutCatalogPreview_PersistsEverySeriesMembershipWithItsAsin()
        {
            await using var fixture = await CreateFixtureAsync();

            var result = await fixture.MonitoringService.MonitorAuthorAsync(new MonitorAuthorRequest
            {
                Name = AuthorName,
                Region = "us",
                Language = "english"
            });

            Assert.Equal(2, result.SyncResult.AddedCount);
            await AssertPersistedMembershipsAsync(fixture);
        }

        [Fact]
        public async Task MonitorAuthorWithCatalogCachedBeforeSeriesEntries_RefetchesAndPersistsEverySeriesMembershipWithItsAsin()
        {
            await using var fixture = await CreateFixtureAsync();

            // A catalog cached by an earlier build: first series only, no identifiers, and no expiry.
            await fixture.AudiobookRepository.UpsertCachedAuthorAsync(new AuthorCacheEntry
            {
                AuthorName = AuthorName,
                AuthorNameNormalized = "h rider haggard",
                AuthorAsin = AuthorAsin,
                Region = "us",
                LastFetchedAt = DateTime.UtcNow.AddDays(-30),
                CatalogBooks = new List<CachedAuthorCatalogBook>
                {
                    new()
                    {
                        Asin = "B00CQ5WAXW",
                        Title = "She And Allan",
                        Authors = new List<string> { AuthorName },
                        Language = "english",
                        Series = "Ayesha",
                        SeriesNumber = "0"
                    },
                    new()
                    {
                        Asin = "B004YWTD30",
                        Title = "She: A History of Adventure",
                        Authors = new List<string> { AuthorName },
                        Language = "english",
                        Series = "Ayesha",
                        SeriesNumber = "1"
                    }
                }
            });

            var result = await fixture.MonitoringService.MonitorAuthorAsync(new MonitorAuthorRequest
            {
                Name = AuthorName,
                Region = "us",
                Language = "english"
            });

            Assert.Equal(2, result.SyncResult.AddedCount);
            fixture.AudibleService.Verify(
                service => service.GetAllBooksByAuthorAsync(
                    AuthorName, AuthorAsin, It.IsAny<int>(), "us", It.IsAny<string?>()),
                Times.Once);

            await AssertPersistedMembershipsAsync(fixture);
        }

        private static async Task AssertPersistedMembershipsAsync(Fixture fixture)
        {
            await using var verifyContext = new ListenArrDbContext(fixture.DbOptions);
            var books = await verifyContext.Audiobooks
                .Include(book => book.SeriesMemberships)
                .AsNoTracking()
                .ToListAsync();

            var dual = Assert.Single(books, book => book.Asin == "B00CQ5WAXW");
            Assert.Collection(
                dual.SeriesMemberships!.OrderBy(membership => membership.SortOrder),
                first =>
                {
                    Assert.Equal("Ayesha", first.SeriesName);
                    Assert.Equal("0", first.SeriesNumber);
                    Assert.Equal("B01E633FQM", first.SeriesAsin);
                    Assert.True(first.IsPrimary);
                },
                second =>
                {
                    Assert.Equal("Allan Quatermain", second.SeriesName);
                    Assert.Null(second.SeriesNumber);
                    Assert.Equal("B01F5TL5K4", second.SeriesAsin);
                    Assert.False(second.IsPrimary);
                });
            Assert.Equal("Ayesha", dual.Series);
            Assert.Equal("0", dual.SeriesNumber);

            var single = Assert.Single(books, book => book.Asin == "B004YWTD30");
            var membership = Assert.Single(single.SeriesMemberships!);
            Assert.Equal("Ayesha", membership.SeriesName);
            Assert.Equal("1", membership.SeriesNumber);
            Assert.Equal("B01E633FQM", membership.SeriesAsin);
        }

        private static async Task<Fixture> CreateFixtureAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var dbOptions = new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(connection).Options;
            var dbContext = new ListenArrDbContext(dbOptions);
            await dbContext.Database.EnsureCreatedAsync();

            var audible = new Mock<AudibleService>(new HttpClient(), Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            audible
                .Setup(service => service.LookupAuthorAsync(AuthorName, "us"))
                .ReturnsAsync(new AuthorLookupItem { Asin = AuthorAsin, Name = AuthorName });
            audible
                .Setup(service => service.GetAllBooksByAuthorAsync(
                    AuthorName, AuthorAsin, It.IsAny<int>(), "us", It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new AudibleSearchResultBuilder()
                            .WithAsin("B00CQ5WAXW")
                            .WithTitle("She And Allan")
                            .WithAuthor(AuthorName)
                            .WithLanguage("english")
                            .WithSeries("Ayesha", "0", "B01E633FQM")
                            .WithSeries("Allan Quatermain", null, "B01F5TL5K4")
                            .Build(),
                        new AudibleSearchResultBuilder()
                            .WithAsin("B004YWTD30")
                            .WithTitle("She: A History of Adventure")
                            .WithAuthor(AuthorName)
                            .WithLanguage("english")
                            .WithSeries("Ayesha", "1", "B01E633FQM")
                            .Build()
                    },
                    TotalResults = 2
                });

            var audiobookRepository = new AudiobookRepository(dbContext);
            var catalogService = new AuthorCatalogService(
                audible.Object,
                Mock.Of<IAudnexusService>(),
                audiobookRepository,
                Mock.Of<ISearchService>(),
                Mock.Of<ILogger<AuthorCatalogService>>());

            var libraryAddService = new Mock<ILibraryAddService>();
            libraryAddService
                .Setup(service => service.AddToLibraryAsync(
                    It.IsAny<LibraryAddOperationRequest>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (LibraryAddOperationRequest request, CancellationToken _) =>
                {
                    var audiobook = request.Metadata!.ToAudiobook();
                    audiobook.Monitored = request.Monitored;
                    await audiobookRepository.AddAsync(audiobook);
                    return new LibraryAddOperationResult { Added = true, Audiobook = audiobook };
                });

            var monitoringService = new AuthorMonitoringService(
                new EfMonitoredAuthorRepository(dbContext),
                audiobookRepository,
                catalogService,
                libraryAddService.Object,
                Mock.Of<ILogger<AuthorMonitoringService>>());

            return new Fixture(connection, dbContext, dbOptions, audible, audiobookRepository, catalogService, monitoringService);
        }

        private sealed class Fixture(
            SqliteConnection connection,
            ListenArrDbContext dbContext,
            DbContextOptions<ListenArrDbContext> dbOptions,
            Mock<AudibleService> audibleService,
            AudiobookRepository audiobookRepository,
            AuthorCatalogService catalogService,
            AuthorMonitoringService monitoringService) : IAsyncDisposable
        {
            public DbContextOptions<ListenArrDbContext> DbOptions { get; } = dbOptions;

            public Mock<AudibleService> AudibleService { get; } = audibleService;

            public AudiobookRepository AudiobookRepository { get; } = audiobookRepository;

            public AuthorCatalogService CatalogService { get; } = catalogService;

            public AuthorMonitoringService MonitoringService { get; } = monitoringService;

            public async ValueTask DisposeAsync()
            {
                await dbContext.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}
