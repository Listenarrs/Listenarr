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
using Listenarr.Application.Metadata;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Name", "SeriesMonitoringServiceTests")]
    [Trait("Category", "SeriesMonitoringService")]
    public class SeriesMonitoringServiceTests : BaseTests
    {
        private readonly Mock<ISeriesCatalogService> _seriesCatalogService = new();
        private readonly Mock<ILibraryAddService> _libraryAddService = new();
        private readonly Mock<IImageCacheService> _imageCacheService = new();

        [Fact]
        public async Task MonitorSeriesAsync_PersistsSeriesAndAddsOnlyMissingBooksForSelectedLanguage()
        {
            // Given
            Init(services => services
                .WithSingleton(_seriesCatalogService.Object)
                .WithSingleton(_libraryAddService.Object));

            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("The Final Empire")
                .WithAuthor("Brandon Sanderson")
                .WithSeries("Mistborn")
                .WithMonitored()
                .Build());

            _seriesCatalogService
                .Setup(service => service.GetCatalogAsync(
                    "Mistborn",
                    "uk",
                    500,
                    null,
                    true,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResult
                {
                    Series = new SeriesLookupItem
                    {
                        Asin = "SERIES123",
                        Name = "Mistborn"
                    },
                    Books =
                    [
                        new AudibleSearchResult
                        {
                            Title = "The Final Empire",
                            Authors = [new AudibleAuthor { Name = "Brandon Sanderson" }],
                            Language = "en-us",
                            Series = [new AudibleSeries { Name = "Mistborn", Position = "1" }]
                        },
                        new AudibleSearchResult
                        {
                            Asin = "BOOK2",
                            Title = "The Well of Ascension",
                            Authors = [new AudibleAuthor { Name = "Brandon Sanderson" }],
                            Language = "english",
                            Series = [new AudibleSeries { Name = "Mistborn", Position = "2" }]
                        },
                        new AudibleSearchResult
                        {
                            Asin = "BOOK3",
                            Title = "Held der Zeiten",
                            Authors = [new AudibleAuthor { Name = "Brandon Sanderson" }],
                            Language = "de",
                            Series = [new AudibleSeries { Name = "Mistborn", Position = "3" }]
                        }
                    ]
                });

            _libraryAddService
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
                    Audiobook = new AudiobookBuilder()
                        .WithTitle("The Well of Ascension")
                        .WithAuthor("Brandon Sanderson")
                        .WithSeries("Mistborn")
                        .WithMonitored()
                        .Build()
                });

            var service = _provider.GetRequiredService<ISeriesMonitoringService>();

            // When
            var result = await service.MonitorSeriesAsync(new MonitorSeriesRequest
            {
                Name = "Mistborn",
                Region = "uk",
                Language = "english"
            });

            // Then
            Assert.NotNull(result.MonitoredSeries);
            Assert.True(result.SyncResult.Succeeded);
            Assert.Equal(1, result.SyncResult.AddedCount);
            Assert.Equal(1, result.SyncResult.ExistingCount);
            Assert.Equal(0, result.SyncResult.FailedCount);
            Assert.Equal("SERIES123", result.MonitoredSeries!.SeriesAsin);
            Assert.Equal("uk", result.MonitoredSeries.Region);
            Assert.Equal("english", result.MonitoredSeries.Language);
            Assert.NotNull(result.MonitoredSeries.LastSuccessfulSyncAt);

            _libraryAddService.Verify(service => service.AddToLibraryAsync(
                    It.IsAny<LibraryAddOperationRequest>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            var monitoredSeriesRepository = _provider.GetRequiredService<IMonitoredSeriesRepository>();
            var storedSeries = Assert.Single(await monitoredSeriesRepository.GetAllAsync());
            Assert.Equal("Mistborn", storedSeries.SeriesName);
            Assert.Equal("mistborn", storedSeries.SeriesNameNormalized);
            Assert.Equal("SERIES123", storedSeries.SeriesAsin);
        }

        [Fact]
        public async Task MonitorSeriesAsync_PersistsTitleFolderInBasePath()
        {
            // Given
            Init(services => services
                .WithSingleton(_seriesCatalogService.Object)
                .WithSingleton(_imageCacheService.Object));

            var rootPath = FileService.GetTempDirectory("series-monitoring-library");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}/{Series}/{Title}")
                .Build());

            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithIsDefault()
                .WithPath(rootPath)
                .Build());

            _seriesCatalogService
                .Setup(service => service.GetCatalogAsync(
                    "Dungeon Crawler Carl",
                    "us",
                    500,
                    null,
                    true,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResult
                {
                    Series = new SeriesLookupItem
                    {
                        Asin = "SERIES123",
                        Name = "Dungeon Crawler Carl"
                    },
                    Books =
                    [
                        new AudibleSearchResult
                        {
                            Asin = "BOOK123",
                            Title = "This Inevitable Ruin",
                            Authors = [new AudibleAuthor { Name = "Matt Dinniman" }],
                            Language = "english",
                            Series =
                            [
                                new AudibleSeries
                                {
                                    Asin = "SERIES123",
                                    Name = "Dungeon Crawler Carl",
                                    Position = "7"
                                }
                            ]
                        }
                    ]
                });

            var service = _provider.GetRequiredService<ISeriesMonitoringService>();

            // When
            var result = await service.MonitorSeriesAsync(new MonitorSeriesRequest
            {
                Name = "Dungeon Crawler Carl",
                Region = "us",
                Language = "english"
            });

            // Then
            Assert.True(result.SyncResult.Succeeded);
            Assert.Equal(1, result.SyncResult.AddedCount);

            var storedAudiobook = Assert.Single(await _audiobookRepository.GetAllAsync());
            Assert.Equal(
                Path.Join(rootPath, "Matt Dinniman", "Dungeon Crawler Carl", "This Inevitable Ruin"),
                storedAudiobook.BasePath);
        }
    }
}
