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
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SeriesMonitoringServiceTests
    {
        [Fact]
        public async Task MonitorSeriesAsync_PersistsSeriesAndAddsOnlyMissingBooksForSelectedLanguage()
        {
            var dbOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: $"series-monitor-{Guid.NewGuid():N}")
                .Options;

            await using var dbContext = new ListenArrDbContext(dbOptions);
            dbContext.Audiobooks.Add(new Audiobook
            {
                Id = 10,
                Title = "The Final Empire",
                Authors = new List<string> { "Brandon Sanderson" },
                Series = "Mistborn",
                Language = "english",
                Monitored = true
            });
            await dbContext.SaveChangesAsync();

            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            seriesCatalogService
                .Setup(service => service.GetCatalogAsync("Mistborn", "uk", 500, null, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResult
                {
                    Series = new SeriesLookupItem
                    {
                        Asin = "SERIES123",
                        Name = "Mistborn"
                    },
                    Books = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Title = "The Final Empire",
                            Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                            Language = "en-us",
                            Series = new List<AudibleSeries> { new() { Name = "Mistborn", Position = "1" } }
                        },
                        new()
                        {
                            Asin = "BOOK2",
                            Title = "The Well of Ascension",
                            Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                            Language = "english",
                            Series = new List<AudibleSeries> { new() { Name = "Mistborn", Position = "2" } }
                        },
                        new()
                        {
                            Asin = "BOOK3",
                            Title = "Held der Zeiten",
                            Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                            Language = "de",
                            Series = new List<AudibleSeries> { new() { Name = "Mistborn", Position = "3" } }
                        }
                    }
                });

            var libraryAddService = new Mock<ILibraryAddService>();
            libraryAddService
                .Setup(service => service.AddToLibraryAsync(
                    It.Is<LibraryAddOperationRequest>(request =>
                        request.Metadata.Title == "The Well of Ascension" &&
                        request.Monitored &&
                        request.HistorySource == "SeriesMonitoring"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LibraryAddOperationResult
                {
                    Added = true,
                    Message = "Audiobook added to library successfully",
                    Audiobook = new Audiobook
                    {
                        Id = 11,
                        Title = "The Well of Ascension",
                        Authors = new List<string> { "Brandon Sanderson" },
                        Series = "Mistborn",
                        Asin = "BOOK2",
                        Monitored = true
                    }
                });

            var seriesRepo = new EfMonitoredSeriesRepository(dbContext);
            var audiobooksRepo = new AudiobookRepository(dbContext);

            var service = new SeriesMonitoringService(
                seriesRepo,
                audiobooksRepo,
                seriesCatalogService.Object,
                libraryAddService.Object,
                Mock.Of<ILogger<SeriesMonitoringService>>());

            var result = await service.MonitorSeriesAsync(new MonitorSeriesRequest
            {
                Name = "Mistborn",
                Region = "uk",
                Language = "english"
            });

            Assert.NotNull(result.MonitoredSeries);
            Assert.True(result.SyncResult.Succeeded);
            Assert.Equal(1, result.SyncResult.AddedCount);
            Assert.Equal(1, result.SyncResult.ExistingCount);
            Assert.Equal(0, result.SyncResult.FailedCount);
            Assert.Equal("SERIES123", result.MonitoredSeries!.SeriesAsin);
            Assert.Equal("uk", result.MonitoredSeries.Region);
            Assert.Equal("english", result.MonitoredSeries.Language);
            Assert.NotNull(result.MonitoredSeries.LastSuccessfulSyncAt);

            libraryAddService.Verify(service => service.AddToLibraryAsync(
                    It.IsAny<LibraryAddOperationRequest>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            var storedSeries = await dbContext.MonitoredSeries.SingleAsync();
            Assert.Equal("Mistborn", storedSeries.SeriesName);
            Assert.Equal("mistborn", storedSeries.SeriesNameNormalized);
            Assert.Equal("SERIES123", storedSeries.SeriesAsin);
        }
    }
}
