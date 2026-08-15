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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Monitoring
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
                .ReturnsAsync(new SeriesCatalogFetchResultBuilder()
                    .WithSeries("Mistborn", "SERIES123")
                    .WithBook(new AudibleSearchResultBuilder()
                        .WithTitle("The Final Empire")
                        .WithAuthor("Brandon Sanderson")
                        .WithLanguage("en-us")
                        .WithSeries("Mistborn", "1")
                        .Build())
                    .WithBook(new AudibleSearchResultBuilder()
                        .WithAsin("BOOK2")
                        .WithTitle("The Well of Ascension")
                        .WithAuthor("Brandon Sanderson")
                        .WithLanguage("english")
                        .WithSeries("Mistborn", "2")
                        .Build())
                    .WithBook(new AudibleSearchResultBuilder()
                        .WithAsin("BOOK3")
                        .WithTitle("Held der Zeiten")
                        .WithAuthor("Brandon Sanderson")
                        .WithLanguage("de")
                        .WithSeries("Mistborn", "3")
                        .Build())
                    .Build());

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
                .ReturnsAsync(new SeriesCatalogFetchResultBuilder()
                    .WithSeries("Dungeon Crawler Carl", "SERIES123")
                    .WithBook(new AudibleSearchResultBuilder()
                        .WithAsin("BOOK123")
                        .WithTitle("This Inevitable Ruin")
                        .WithAuthor("Matt Dinniman")
                        .WithLanguage("english")
                        .WithSeries("Dungeon Crawler Carl", "7", "SERIES123")
                        .Build())
                    .Build());

            var service = _provider.GetRequiredService<ISeriesMonitoringService>();

            // When
            var result = await service.MonitorSeriesAsync(new MonitorSeriesRequest
            {
                Name = "Dungeon Crawler Carl",
                Region = "us",
                Language = "english"
            });

            // Then
            Assert.True(
                result.SyncResult.Succeeded,
                result.SyncResult.ErrorMessage);
            Assert.Equal(1, result.SyncResult.AddedCount);

            var storedAudiobook = Assert.Single(await _audiobookRepository.GetAllAsync());
            Assert.Equal(
                Path.Join(rootPath, "Matt Dinniman", "Dungeon Crawler Carl", "This Inevitable Ruin"),
                storedAudiobook.BasePath);
        }
    }
}
