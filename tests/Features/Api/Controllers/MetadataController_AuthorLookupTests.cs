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
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class MetadataController_AuthorLookupTests
    {
        private static readonly HttpClient SharedAudibleHttpClient = new();

        private static Mock<AudibleService> CreateAudibleServiceMock()
        {
            return new Mock<AudibleService>(SharedAudibleHttpClient, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
        }

        [Fact]
        public async Task LookupAuthor_PrefersCachedImage_AndReturnsBiographyAndSimilarAuthors()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = CreateAudibleServiceMock();
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            audiobookRepository
                .Setup(repository => repository.GetAuthorAsinByNameAsync("Andy Weir"))
                .ReturnsAsync("AUTHOR123");

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("AUTHOR123"))
                .ReturnsAsync("config/cache/images/authors/AUTHOR123.jpg");

            audible
                .Setup(service => service.GetAuthorByAsinAsync("AUTHOR123", "us"))
                .ReturnsAsync(new AuthorLookupItem
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Image = "https://example.com/andy-weir.jpg",
                    Description = "Audible biography"
                });

            audnexus
                .Setup(service => service.GetAuthorAsync("AUTHOR123", "us", false))
                .ReturnsAsync(new AudnexusAuthorResponse
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Similar = new List<AudnexusSimilarAuthor>
                    {
                        new() { Asin = "SIM123", Name = "Blake Crouch" },
                        new() { Asin = "SIM999", Name = "Ernest Cline" }
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

            var result = await controller.LookupAuthor("Andy Weir", "us");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorLookupResponse>(ok.Value);

            Assert.Equal("AUTHOR123", payload.Asin);
            Assert.Equal("Andy Weir", payload.Name);
            Assert.Equal("/config/cache/images/authors/AUTHOR123.jpg", payload.CachedPath);
            Assert.Equal("Audible biography", payload.Description);
            Assert.Collection(
                payload.SimilarAuthors,
                author => Assert.Equal("Blake Crouch", author.Name),
                author => Assert.Equal("Ernest Cline", author.Name));

            imageCache.Verify(
                service => service.MoveToAuthorLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never);
        }

        [Fact]
        public async Task LookupAuthor_UsesPersistedAuthorCache_BeforeAudible()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = CreateAudibleServiceMock();
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedAuthorByNameAsync("Andy Weir", "us"))
                .ReturnsAsync(new AuthorCacheEntry
                {
                    AuthorName = "Andy Weir",
                    AuthorNameNormalized = "andy weir",
                    AuthorAsin = "AUTHOR123",
                    Region = "us",
                    ImageUrl = "https://example.com/andy-weir.jpg",
                    Description = "Persisted biography",
                    SimilarAuthors = new List<CachedRelatedAuthor>
                    {
                        new() { Asin = "SIM123", Name = "Blake Crouch" }
                    }
                });

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("AUTHOR123"))
                .ReturnsAsync("config/cache/images/authors/AUTHOR123.jpg");

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

            var result = await controller.LookupAuthor("Andy Weir", "us");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorLookupResponse>(ok.Value);

            Assert.Equal("AUTHOR123", payload.Asin);
            Assert.Equal("Andy Weir", payload.Name);
            Assert.Equal("Persisted biography", payload.Description);
            Assert.Equal("/config/cache/images/authors/AUTHOR123.jpg", payload.CachedPath);
            Assert.Single(payload.SimilarAuthors);

            audible.Verify(service => service.LookupAuthorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            audible.Verify(service => service.GetAuthorByAsinAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task LookupAuthor_RefreshesMissingCachedImage_FromAudibleAndStoresIt()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = CreateAudibleServiceMock();
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedAuthorByNameAsync("Andy Weir", "us"))
                .ReturnsAsync(new AuthorCacheEntry
                {
                    AuthorName = "Andy Weir",
                    AuthorNameNormalized = "andy weir",
                    AuthorAsin = "AUTHOR123",
                    Region = "us",
                    ImageUrl = "https://db.example.com/andy-weir.jpg",
                    Description = "Persisted biography",
                    SimilarAuthors = new List<CachedRelatedAuthor>
                    {
                        new() { Asin = "SIM123", Name = "Blake Crouch" }
                    }
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedAuthorAsync(It.IsAny<AuthorCacheEntry>()))
                .ReturnsAsync((AuthorCacheEntry entry) => entry);

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("AUTHOR123"))
                .ReturnsAsync((string?)null);

            imageCache
                .Setup(service => service.MoveToAuthorLibraryStorageAsync("AUTHOR123", "https://audible.example.com/andy-weir.jpg"))
                .ReturnsAsync("config/cache/images/authors/AUTHOR123.jpg");

            audible
                .Setup(service => service.GetAuthorByAsinAsync("AUTHOR123", "us"))
                .ReturnsAsync(new AuthorLookupItem
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Image = "https://audible.example.com/andy-weir.jpg",
                    Description = "Audible biography"
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

            var result = await controller.LookupAuthor("Andy Weir", "us");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorLookupResponse>(ok.Value);

            Assert.Equal("/config/cache/images/authors/AUTHOR123.jpg", payload.CachedPath);
            Assert.Equal("https://audible.example.com/andy-weir.jpg", payload.Image);
            Assert.Equal("Audible biography", payload.Description);

            imageCache.Verify(
                service => service.MoveToAuthorLibraryStorageAsync("AUTHOR123", "https://audible.example.com/andy-weir.jpg"),
                Times.Once);
            audnexus.Verify(service => service.GetAuthorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
            audiobookRepository.Verify(
                repository => repository.UpsertCachedAuthorAsync(It.Is<AuthorCacheEntry>(entry =>
                    entry.AuthorAsin == "AUTHOR123" &&
                    entry.ImageUrl == "https://audible.example.com/andy-weir.jpg")),
                Times.Once);
        }

        [Fact]
        public async Task LookupAuthor_RefreshesPersistedBiographyOnlyEntry_ToLoadSimilarAuthors()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = CreateAudibleServiceMock();
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedAuthorByNameAsync("Andy Weir", "us"))
                .ReturnsAsync(new AuthorCacheEntry
                {
                    AuthorName = "Andy Weir",
                    AuthorNameNormalized = "andy weir",
                    AuthorAsin = "AUTHOR123",
                    Region = "us",
                    ImageUrl = "https://example.com/andy-weir.jpg",
                    Description = "Persisted Audible biography",
                    SimilarAuthors = new List<CachedRelatedAuthor>()
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedAuthorAsync(It.IsAny<AuthorCacheEntry>()))
                .ReturnsAsync((AuthorCacheEntry entry) => entry);

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("AUTHOR123"))
                .ReturnsAsync("config/cache/images/authors/AUTHOR123.jpg");

            audible
                .Setup(service => service.GetAuthorByAsinAsync("AUTHOR123", "us"))
                .ReturnsAsync(new AuthorLookupItem
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Image = "https://example.com/andy-weir.jpg",
                    Description = "Persisted Audible biography"
                });

            audnexus
                .Setup(service => service.GetAuthorAsync("AUTHOR123", "us", false))
                .ReturnsAsync(new AudnexusAuthorResponse
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Description = "Audnexus biography that should be ignored",
                    Similar = new List<AudnexusSimilarAuthor>
                    {
                        new() { Asin = "SIM123", Name = "Blake Crouch" },
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

            var result = await controller.LookupAuthor("Andy Weir", "us");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorLookupResponse>(ok.Value);

            Assert.Equal("Persisted Audible biography", payload.Description);
            Assert.Single(payload.SimilarAuthors);
            Assert.Equal("Blake Crouch", payload.SimilarAuthors[0].Name);

            audnexus.Verify(service => service.GetAuthorAsync("AUTHOR123", "us", false), Times.Once);
        }

        [Fact]
        public async Task LookupAuthor_RefreshesPartialPersistedCache_WithBiographyAndSimilarAuthors()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = CreateAudibleServiceMock();
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedAuthorByNameAsync("Andy Weir", "us"))
                .ReturnsAsync(new AuthorCacheEntry
                {
                    AuthorName = "Andy Weir",
                    AuthorNameNormalized = "andy weir",
                    AuthorAsin = "AUTHOR123",
                    Region = "us",
                    ImageUrl = "https://example.com/andy-weir.jpg",
                    Description = null,
                    SimilarAuthors = new List<CachedRelatedAuthor>()
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedAuthorAsync(It.IsAny<AuthorCacheEntry>()))
                .ReturnsAsync((AuthorCacheEntry entry) => entry);

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("AUTHOR123"))
                .ReturnsAsync("config/cache/images/authors/AUTHOR123.jpg");

            audible
                .Setup(service => service.GetAuthorByAsinAsync("AUTHOR123", "us"))
                .ReturnsAsync(new AuthorLookupItem
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Image = "https://example.com/andy-weir.jpg"
                });

            audnexus
                .Setup(service => service.GetAuthorAsync("AUTHOR123", "us", false))
                .ReturnsAsync(new AudnexusAuthorResponse
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Description = "Audnexus biography",
                    Similar = new List<AudnexusSimilarAuthor>
                    {
                        new() { Asin = "SIM123", Name = "Blake Crouch" },
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

            var result = await controller.LookupAuthor("Andy Weir", "us");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorLookupResponse>(ok.Value);

            Assert.Equal("Audnexus biography", payload.Description);
            Assert.Single(payload.SimilarAuthors);
            Assert.Equal("Blake Crouch", payload.SimilarAuthors[0].Name);

            audnexus.Verify(service => service.GetAuthorAsync("AUTHOR123", "us", false), Times.Once);
            audiobookRepository.Verify(
                repository => repository.UpsertCachedAuthorAsync(It.Is<AuthorCacheEntry>(entry =>
                    entry.AuthorAsin == "AUTHOR123" &&
                    entry.Description == "Audnexus biography" &&
                    entry.SimilarAuthors != null &&
                    entry.SimilarAuthors.Count == 1)),
                Times.Once);
        }

        [Fact]
        public async Task LookupAuthor_FallsBackToAudnexus_WhenAudibleCannotHydrateMissingFields()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = CreateAudibleServiceMock();
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedAuthorByNameAsync("Andy Weir", "us"))
                .ReturnsAsync(new AuthorCacheEntry
                {
                    AuthorName = "Andy Weir",
                    AuthorNameNormalized = "andy weir",
                    AuthorAsin = "AUTHOR123",
                    Region = "us",
                    ImageUrl = null,
                    Description = null,
                    SimilarAuthors = new List<CachedRelatedAuthor>()
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedAuthorAsync(It.IsAny<AuthorCacheEntry>()))
                .ReturnsAsync((AuthorCacheEntry entry) => entry);

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("AUTHOR123"))
                .ReturnsAsync((string?)null);

            imageCache
                .Setup(service => service.MoveToAuthorLibraryStorageAsync("AUTHOR123", "https://audnexus.example.com/andy-weir.jpg"))
                .ReturnsAsync("config/cache/images/authors/AUTHOR123.jpg");

            audible
                .Setup(service => service.GetAuthorByAsinAsync("AUTHOR123", "us"))
                .ReturnsAsync((AuthorLookupItem?)null);

            audible
                .Setup(service => service.LookupAuthorAsync("Andy Weir", "us"))
                .ReturnsAsync((AuthorLookupItem?)null);

            audnexus
                .Setup(service => service.GetAuthorAsync("AUTHOR123", "us", false))
                .ReturnsAsync(new AudnexusAuthorResponse
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Description = "Audnexus biography",
                    Image = "https://audnexus.example.com/andy-weir.jpg",
                    Similar = new List<AudnexusSimilarAuthor>
                    {
                        new() { Asin = "SIM123", Name = "Blake Crouch" }
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

            var result = await controller.LookupAuthor("Andy Weir", "us");

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorLookupResponse>(ok.Value);

            Assert.Equal("/config/cache/images/authors/AUTHOR123.jpg", payload.CachedPath);
            Assert.Equal("https://audnexus.example.com/andy-weir.jpg", payload.Image);
            Assert.Equal("Audnexus biography", payload.Description);
            Assert.Single(payload.SimilarAuthors);
            Assert.Equal("Blake Crouch", payload.SimilarAuthors[0].Name);

            imageCache.Verify(
                service => service.MoveToAuthorLibraryStorageAsync("AUTHOR123", "https://audnexus.example.com/andy-weir.jpg"),
                Times.Once);
            audiobookRepository.Verify(
                repository => repository.UpsertCachedAuthorAsync(It.Is<AuthorCacheEntry>(entry =>
                    entry.AuthorAsin == "AUTHOR123" &&
                    entry.ImageUrl == "https://audnexus.example.com/andy-weir.jpg" &&
                    entry.Description == "Audnexus biography" &&
                    entry.SimilarAuthors != null &&
                    entry.SimilarAuthors.Count == 1)),
                Times.Once);
        }

        [Fact]
        public async Task LookupAuthor_RefreshRequest_BypassesCachedLookupAndRecachesFreshMetadata()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = CreateAudibleServiceMock();
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedAuthorByNameAsync("Andy Weir", "us"))
                .ReturnsAsync(new AuthorCacheEntry
                {
                    AuthorName = "Andy Weir",
                    AuthorNameNormalized = "andy weir",
                    AuthorAsin = "AUTHOR123",
                    Region = "us",
                    ImageUrl = "https://old.example.com/andy-weir.jpg",
                    Description = "Old biography",
                    SimilarAuthors = new List<CachedRelatedAuthor>
                    {
                        new() { Asin = "SIMOLD", Name = "Old Similar Author" }
                    }
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedAuthorAsync(It.IsAny<AuthorCacheEntry>()))
                .ReturnsAsync((AuthorCacheEntry entry) => entry);

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("AUTHOR123"))
                .ReturnsAsync("config/cache/images/authors/AUTHOR123.jpg");

            imageCache
                .Setup(service => service.MoveToAuthorLibraryStorageAsync("AUTHOR123", "https://fresh.example.com/andy-weir.jpg", true))
                .ReturnsAsync("config/cache/images/authors/AUTHOR123.jpg");

            audible
                .Setup(service => service.GetAuthorByAsinAsync("AUTHOR123", "us"))
                .ReturnsAsync(new AuthorLookupItem
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Image = "https://fresh.example.com/andy-weir.jpg",
                    Description = "Fresh biography"
                });

            audnexus
                .Setup(service => service.GetAuthorAsync("AUTHOR123", "us", false))
                .ReturnsAsync(new AudnexusAuthorResponse
                {
                    Asin = "AUTHOR123",
                    Name = "Andy Weir",
                    Similar = new List<AudnexusSimilarAuthor>
                    {
                        new() { Asin = "SIMNEW", Name = "Blake Crouch" }
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

            var result = await controller.RefreshAuthor(new MetadataController.AuthorLookupRefreshRequest
            {
                Name = "Andy Weir",
                Region = "us"
            });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<MetadataController.AuthorLookupResponse>(ok.Value);

            Assert.Equal("Fresh biography", payload.Description);
            Assert.Single(payload.SimilarAuthors);
            Assert.Equal("Blake Crouch", payload.SimilarAuthors[0].Name);
            Assert.Equal("/config/cache/images/authors/AUTHOR123.jpg", payload.CachedPath);

            audible.Verify(service => service.GetAuthorByAsinAsync("AUTHOR123", "us"), Times.Once);
            imageCache.Verify(
                service => service.MoveToAuthorLibraryStorageAsync("AUTHOR123", "https://fresh.example.com/andy-weir.jpg", true),
                Times.Once);
            audiobookRepository.Verify(
                repository => repository.UpsertCachedAuthorAsync(It.Is<AuthorCacheEntry>(entry =>
                    entry.AuthorAsin == "AUTHOR123" &&
                    entry.Description == "Fresh biography" &&
                    entry.SimilarAuthors != null &&
                    entry.SimilarAuthors.Count == 1 &&
                    entry.SimilarAuthors[0].Name == "Blake Crouch")),
                Times.Once);
        }

        [Fact]
        public async Task LookupAuthor_WhenAsinIsProvided_DoesNotReuseNameOnlyMemoryCacheEntry()
        {
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var metadataService = new Mock<IAudiobookMetadataService>();
            var audible = CreateAudibleServiceMock();
            var audnexus = new Mock<IAudnexusService>();
            var imageCache = new Mock<IImageCacheService>();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var asinLookup = new Mock<IAsinLookupService>();
            var authorCatalogService = new Mock<IAuthorCatalogService>();
            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            var logger = new Mock<ILogger<MetadataController>>();

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("AUTHOR1"))
                .ReturnsAsync("config/cache/images/authors/AUTHOR1.jpg");

            imageCache
                .Setup(service => service.GetCachedImagePathAsync("AUTHOR2"))
                .ReturnsAsync("config/cache/images/authors/AUTHOR2.jpg");

            audible
                .Setup(service => service.LookupAuthorAsync("Alex Smith", "us"))
                .ReturnsAsync(new AuthorLookupItem
                {
                    Asin = "AUTHOR1",
                    Name = "Alex Smith",
                    Image = "https://example.com/author-1.jpg",
                    Description = "First Alex Smith"
                });

            audible
                .Setup(service => service.GetAuthorByAsinAsync("AUTHOR2", "us"))
                .ReturnsAsync(new AuthorLookupItem
                {
                    Asin = "AUTHOR2",
                    Name = "Alex Smith",
                    Image = "https://example.com/author-2.jpg",
                    Description = "Second Alex Smith"
                });

            audnexus
                .Setup(service => service.GetAuthorAsync("AUTHOR1", "us", false))
                .ReturnsAsync(new AudnexusAuthorResponse
                {
                    Asin = "AUTHOR1",
                    Name = "Alex Smith",
                    Similar = new List<AudnexusSimilarAuthor>
                    {
                        new() { Asin = "SIM1", Name = "Author One Friend" }
                    }
                });

            audnexus
                .Setup(service => service.GetAuthorAsync("AUTHOR2", "us", false))
                .ReturnsAsync(new AudnexusAuthorResponse
                {
                    Asin = "AUTHOR2",
                    Name = "Alex Smith",
                    Similar = new List<AudnexusSimilarAuthor>
                    {
                        new() { Asin = "SIM2", Name = "Author Two Friend" }
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

            var initialResult = await controller.LookupAuthor("Alex Smith", "us");
            var initialOk = Assert.IsType<OkObjectResult>(initialResult.Result);
            var initialPayload = Assert.IsType<MetadataController.AuthorLookupResponse>(initialOk.Value);
            Assert.Equal("AUTHOR1", initialPayload.Asin);

            var specificResult = await controller.LookupAuthor("Alex Smith", "us", "AUTHOR2");
            var specificOk = Assert.IsType<OkObjectResult>(specificResult.Result);
            var specificPayload = Assert.IsType<MetadataController.AuthorLookupResponse>(specificOk.Value);

            Assert.Equal("AUTHOR2", specificPayload.Asin);
            Assert.Equal("Second Alex Smith", specificPayload.Description);
            Assert.Equal("/config/cache/images/authors/AUTHOR2.jpg", specificPayload.CachedPath);
            Assert.Single(specificPayload.SimilarAuthors);
            Assert.Equal("Author Two Friend", specificPayload.SimilarAuthors[0].Name);

            audible.Verify(service => service.GetAuthorByAsinAsync("AUTHOR2", "us"), Times.Once);
        }
    }
}
