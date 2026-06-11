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
using Listenarr.Api.Controllers;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class MetadataController_SeriesTests
    {
        private static readonly HttpClient SharedHttpClient = new();

        [Fact]
        public async Task LookupSeries_UsesPersistedSeriesCache_BeforeAudible()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = new Mock<AudibleService>(SharedHttpClient, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Mistborn", "us"))
                .ReturnsAsync(new SeriesCacheEntry
                {
                    SeriesName = "Mistborn",
                    SeriesNameNormalized = "mistborn",
                    SeriesAsin = "SERIES123",
                    Region = "us",
                    ImageUrl = "https://example.com/mistborn.jpg",
                    Description = "Persisted series description",
                    CatalogBooks = new List<CachedSeriesCatalogBook>
                    {
                        new() { Title = "The Final Empire" }
                    }
                });

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("SERIES123"))
                .ReturnsAsync("config/cache/images/series/SERIES123.jpg");

            var controller = new MetadataController(
                metadataService.Object,
                audible.Object,
                audnexus.Object,
                imageCache.Object,
                memoryCache,
                audiobookRepository.Object,
                asinLookup.Object,
                authorCatalogService.Object,
                seriesCatalogService.Object,
                logger.Object);

            var result = await controller.LookupSeries("Mistborn", "us");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.SeriesLookupResponse>(ok.Value);

            Assert.Equal("SERIES123", payload.Asin);
            Assert.Equal("Mistborn", payload.Name);
            Assert.Equal("/config/cache/images/series/SERIES123.jpg", payload.CachedPath);
            Assert.Equal("Persisted series description", payload.Description);
            Assert.Equal(1, payload.TotalBooks);

            audible.Verify(service => service.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task LookupSeries_RefreshesSeriesImage_FromCatalogAndPersistsIt()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = new Mock<AudibleService>(SharedHttpClient, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Mistborn", "us"))
                .ReturnsAsync(new SeriesCacheEntry
                {
                    SeriesName = "Mistborn",
                    SeriesNameNormalized = "mistborn",
                    SeriesAsin = "SERIES123",
                    Region = "us",
                    ImageUrl = null,
                    Description = null
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedSeriesAsync(It.IsAny<SeriesCacheEntry>()))
                .ReturnsAsync((SeriesCacheEntry entry) => entry);

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("SERIES123"))
                .ReturnsAsync((string?)null);

            imageCache
                .Setup(service => service.MoveToSeriesLibraryStorageAsync("SERIES123", "https://example.com/final-empire.jpg", true))
                .ReturnsAsync("config/cache/images/series/SERIES123.jpg");

            audible
                .Setup(service => service.GetSeriesByAsinAsync("SERIES123", "us"))
                .ReturnsAsync(new SeriesLookupItem
                {
                    Asin = "SERIES123",
                    Name = "Mistborn",
                    Description = "Fresh series description"
                });

            seriesCatalogService
                .Setup(service => service.GetCatalogAsync("Mistborn", "us", 250, null, true, It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResult
                {
                    Series = new SeriesLookupItem
                    {
                        Asin = "SERIES123",
                        Name = "Mistborn",
                        Description = "Fresh series description"
                    },
                    Books = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Asin = "BOOK1",
                            Title = "The Final Empire",
                            ImageUrl = "https://example.com/final-empire.jpg"
                        }
                    }
                });

            var controller = new MetadataController(
                metadataService.Object,
                audible.Object,
                audnexus.Object,
                imageCache.Object,
                memoryCache,
                audiobookRepository.Object,
                asinLookup.Object,
                authorCatalogService.Object,
                seriesCatalogService.Object,
                logger.Object);

            var result = await controller.RefreshSeries(new MetadataController.SeriesLookupRefreshRequest
            {
                Name = "Mistborn",
                Region = "us"
            });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.SeriesLookupResponse>(ok.Value);

            Assert.Equal("/config/cache/images/series/SERIES123.jpg", payload.CachedPath);
            Assert.Equal("https://example.com/final-empire.jpg", payload.Image);
            Assert.Equal("Fresh series description", payload.Description);
            Assert.Equal(1, payload.TotalBooks);

            imageCache.Verify(
                service => service.MoveToSeriesLibraryStorageAsync("SERIES123", "https://example.com/final-empire.jpg", true),
                Times.Once);
            audiobookRepository.Verify(
                repository => repository.UpsertCachedSeriesAsync(It.Is<SeriesCacheEntry>(entry =>
                    entry.SeriesAsin == "SERIES123" &&
                    entry.ImageUrl == "https://example.com/final-empire.jpg" &&
                    entry.Description == "Fresh series description" &&
                    entry.CatalogBooks != null &&
                    entry.CatalogBooks.Count == 1)),
                Times.Once);
        }

        [Fact]
        public async Task GetSeriesBooks_MapsResponse()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = new Mock<AudibleService>(SharedHttpClient, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            seriesCatalogService
                .Setup(service => service.GetCatalogAsync("Mistborn", "us", 5, null, false, It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResult
                {
                    Series = new SeriesLookupItem
                    {
                        Asin = "SERIES123",
                        Name = "Mistborn",
                        Image = "mistborn.jpg",
                        Description = "Series description"
                    },
                    Books = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Asin = "BOOK1",
                            Title = "The Final Empire",
                            ImageUrl = "book1.jpg",
                            Language = "english",
                            Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                            Narrators = new List<AudibleNarrator> { new() { Name = "Narrator Example" } },
                            Series = new List<AudibleSeries> { new() { Name = "Mistborn", Position = "1" } }
                        }
                    }
                });

            var controller = new MetadataController(
                metadataService.Object,
                audible.Object,
                audnexus.Object,
                imageCache.Object,
                memoryCache,
                audiobookRepository.Object,
                asinLookup.Object,
                authorCatalogService.Object,
                seriesCatalogService.Object,
                logger.Object);

            var result = await controller.GetSeriesBooks("Mistborn", "us", 5);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.SeriesCatalogResponse>(ok.Value);

            Assert.Equal("SERIES123", payload.Series.Asin);
            Assert.Equal("Mistborn", payload.Series.Name);
            Assert.Equal("Series description", payload.Series.Description);
            Assert.Single(payload.Books);
            Assert.Equal("The Final Empire", payload.Books[0].Title);
            Assert.Equal("Mistborn", payload.Books[0].Series);
            Assert.Equal("1", payload.Books[0].SeriesNumber);
        }
    }
}
