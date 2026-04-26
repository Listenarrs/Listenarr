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
using Listenarr.Api.Services;
using Listenarr.Application.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class MetadataController_AuthorCatalogTests
    {
        private static readonly HttpClient SharedHttpClient = new();

        [Fact]
        public async Task GetAuthorBooks_AggregatesPagedResults_AndMapsResponse()
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

            authorCatalogService
                .Setup(service => service.GetCatalogAsync("SenLinYu", "us", 5, null, false, It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new AuthorCatalogFetchResult
                {
                    Author = new AuthorLookupItem
                    {
                        Asin = "AUTHOR123",
                        Name = "SenLinYu",
                        Image = "https://example.com/authors/senlinyu.jpg"
                    },
                    Books = new List<AudibleSearchResult>
                    {
                        CreateBook("B000000001", "Book 1", "Series Name", "1"),
                        CreateBook("B000000002", "Book 2", "Series Name", "2"),
                        CreateBook("B000000003", "Book 3", "Series Name", "3"),
                        CreateBook("B000000004", "Book 4", "Series Name", "4"),
                        CreateBook("B000000005", "Book 5", "Series Name", "5"),
                    },
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

            var result = await controller.GetAuthorBooks("SenLinYu", "us", 5);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorCatalogResponse>(ok.Value);

            Assert.Equal("AUTHOR123", payload.Author.Asin);
            Assert.Equal("SenLinYu", payload.Author.Name);
            Assert.Equal(5, payload.TotalBooks);
            Assert.Equal(5, payload.Books.Count);
            Assert.Equal("Book 1", payload.Books[0].Title);
            Assert.Equal("Series Name", payload.Books[0].Series);
            Assert.Equal("1", payload.Books[0].SeriesNumber);
            Assert.Equal("Audible", payload.Books[0].MetadataSource);
            Assert.Contains("SenLinYu", payload.Books[0].Authors);
            Assert.Contains("Narrator Example", payload.Books[0].Narrators);
        }

        [Fact]
        public async Task GetAuthorBooks_UsesRepositoryAuthorAsinFallback_WhenLookupMissing()
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

            authorCatalogService
                .Setup(service => service.GetCatalogAsync("Fallback Author", "us", 1, null, false, It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new AuthorCatalogFetchResult
                {
                    Author = new AuthorLookupItem
                    {
                        Asin = "AUTHOR999",
                        Name = "Fallback Author"
                    },
                    Books = new List<AudibleSearchResult>
                    {
                        CreateBook("B000009999", "Fallback Book")
                    },
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

            var result = await controller.GetAuthorBooks("Fallback Author", "us", 1);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorCatalogResponse>(ok.Value);

            Assert.Equal("AUTHOR999", payload.Author.Asin);
            Assert.Single(payload.Books);
            Assert.Equal("Fallback Book", payload.Books[0].Title);
        }

        [Fact]
        public async Task GetAuthorBooks_RefreshRequest_ForcesCatalogRefresh()
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

            authorCatalogService
                .Setup(service => service.GetCatalogAsync("Andy Weir", "us", 50, null, true, It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new AuthorCatalogFetchResult
                {
                    Author = new AuthorLookupItem
                    {
                        Asin = "AUTHOR123",
                        Name = "Andy Weir"
                    },
                    Books = new List<AudibleSearchResult>
                    {
                        CreateBook("B000000123", "Project Hail Mary")
                    },
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

            var result = await controller.RefreshAuthorBooks(new MetadataController.CatalogRefreshRequest
            {
                Name = "Andy Weir",
                Region = "us",
                Limit = 50
            });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorCatalogResponse>(ok.Value);

            Assert.Single(payload.Books);
            Assert.Equal("Project Hail Mary", payload.Books[0].Title);
            authorCatalogService.Verify(
                service => service.GetCatalogAsync("Andy Weir", "us", 50, null, true, It.IsAny<System.Threading.CancellationToken>()),
                Times.Once);
        }

        private static AudibleSearchResult CreateBook(string asin, string title, string? series = null, string? seriesNumber = null)
        {
            return new AudibleSearchResult
            {
                Asin = asin,
                Title = title,
                ImageUrl = $"https://example.com/books/{asin}.jpg",
                Language = "english",
                Publisher = "Podium",
                ReleaseDate = "2024-01-01",
                Isbn = $"978{asin.Substring(Math.Max(0, asin.Length - 7))}",
                Link = $"https://audible.example/{asin}",
                Authors = new List<AudibleAuthor>
                {
                    new AudibleAuthor { Name = "SenLinYu" }
                },
                Narrators = new List<AudibleNarrator>
                {
                    new AudibleNarrator { Name = "Narrator Example" }
                },
                Genres = new List<AudibleGenre>
                {
                    new AudibleGenre { Name = "Fantasy" }
                },
                Series = string.IsNullOrWhiteSpace(series)
                    ? null
                    : new List<AudibleSeries>
                    {
                        new AudibleSeries { Name = series, Position = seriesNumber }
                    }
            };
        }
    }
}
