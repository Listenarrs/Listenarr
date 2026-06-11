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
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SeriesCatalogServiceTests
    {
        [Fact]
        public async Task GetCatalogAsync_UsesPersistedCatalogCache_BeforeAudible()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Mistborn", "us"))
                .ReturnsAsync(new SeriesCacheEntry
                {
                    SeriesName = "Mistborn",
                    SeriesNameNormalized = "mistborn",
                    SeriesAsin = "SERIES123",
                    Region = "us",
                    ImageUrl = "mistborn.jpg",
                    Description = "Persisted series description",
                    CatalogBooks = new List<CachedSeriesCatalogBook>
                    {
                        new()
                        {
                            Asin = "BOOK1",
                            Title = "The Final Empire",
                            Authors = new List<string> { "Brandon Sanderson" },
                            Language = "english"
                        },
                        new()
                        {
                            Asin = "BOOK2",
                            Title = "The Well of Ascension",
                            Authors = new List<string> { "Brandon Sanderson" },
                            Language = "german"
                        }
                    }
                });

            var service = new SeriesCatalogService(
                audible.Object,
                audiobookRepository.Object,
                logger.Object);

            var result = await service.GetCatalogAsync("Mistborn", "us", 10, "english");

            Assert.NotNull(result);
            Assert.Single(result!.Books);
            Assert.Equal("The Final Empire", result.Books[0].Title);
            Assert.Equal("Mistborn", result.Series.Name);

            audible.Verify(service => service.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            audible.Verify(service => service.GetTypedBooksBySeriesAsinAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task GetCatalogAsync_ForceRefresh_BypassesPersistedCatalogCache_AndPersistsFreshBooks()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Mistborn", "us"))
                .ReturnsAsync(new SeriesCacheEntry
                {
                    SeriesName = "Mistborn",
                    SeriesNameNormalized = "mistborn",
                    SeriesAsin = "SERIES123",
                    Region = "us",
                    ImageUrl = "old-mistborn.jpg",
                    CatalogBooks = new List<CachedSeriesCatalogBook>
                    {
                        new()
                        {
                            Title = "Old Cached Book",
                            Authors = new List<string> { "Brandon Sanderson" },
                            MetadataSource = "OpenLibrary"
                        }
                    }
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedSeriesAsync(It.IsAny<SeriesCacheEntry>()))
                .ReturnsAsync((SeriesCacheEntry entry) => entry);

            audible
                .Setup(service => service.GetTypedBooksBySeriesAsinAsync("SERIES123", "us"))
                .ReturnsAsync(new List<AudibleSearchResult>
                {
                    new()
                    {
                        Asin = "BOOK1",
                        Title = "The Final Empire",
                        Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                        ImageUrl = "final-empire.jpg",
                        Language = "english",
                        Link = "https://audible.example/final-empire",
                        Series = new List<AudibleSeries> { new() { Name = "Mistborn", Position = "1" } }
                    }
                });

            var service = new SeriesCatalogService(
                audible.Object,
                audiobookRepository.Object,
                logger.Object);

            var result = await service.GetCatalogAsync("Mistborn", "us", 10, forceRefresh: true);

            Assert.NotNull(result);
            Assert.Single(result!.Books);
            Assert.Equal("The Final Empire", result.Books[0].Title);

            audible.Verify(
                svc => svc.GetTypedBooksBySeriesAsinAsync("SERIES123", "us"),
                Times.Once);
            audiobookRepository.Verify(
                repository => repository.UpsertCachedSeriesAsync(It.Is<SeriesCacheEntry>(entry =>
                    entry.SeriesAsin == "SERIES123" &&
                    entry.CatalogBooks != null &&
                    entry.CatalogBooks.Count == 1 &&
                    entry.CatalogBooks[0].Title == "The Final Empire")),
                Times.Once);
        }

        [Fact]
        public async Task GetCatalogAsync_UsesPositionForRequestedSeries_NotThePrimary()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            // Resolve the requested series ("Mistborn" = SERIES123) via the persisted cache entry.
            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Mistborn", "us"))
                .ReturnsAsync(new SeriesCacheEntry
                {
                    SeriesName = "Mistborn",
                    SeriesNameNormalized = "mistborn",
                    SeriesAsin = "SERIES123",
                    Region = "us",
                    CatalogBooks = new List<CachedSeriesCatalogBook>
                    {
                        new() { Title = "stale", Authors = new List<string> { "Brandon Sanderson" } }
                    }
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedSeriesAsync(It.IsAny<SeriesCacheEntry>()))
                .ReturnsAsync((SeriesCacheEntry entry) => entry);

            // The fetched book belongs to two series; its PRIMARY (first) entry is a different
            // series, and the series being viewed ("Mistborn") is second, at position 3.
            audible
                .Setup(service => service.GetTypedBooksBySeriesAsinAsync("SERIES123", "us"))
                .ReturnsAsync(new List<AudibleSearchResult>
                {
                    new()
                    {
                        Asin = "BOOK1",
                        Title = "The Alloy of Law",
                        Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                        Language = "english",
                        Series = new List<AudibleSeries>
                        {
                            new() { Asin = "OTHER999", Name = "Wax and Wayne", Position = "5" },
                            new() { Asin = "SERIES123", Name = "Mistborn", Position = "3" }
                        }
                    }
                });

            var service = new SeriesCatalogService(
                audible.Object,
                audiobookRepository.Object,
                logger.Object);

            var result = await service.GetCatalogAsync("Mistborn", "us", 10, forceRefresh: true);

            Assert.NotNull(result);
            var book = Assert.Single(result!.Books);

            // The catalog reflects the book's position in the requested series (Mistborn #3),
            // not its primary series (Wax and Wayne #5).
            Assert.Equal("Mistborn", book.Series!.First().Name);
            Assert.Equal("3", book.Series!.First().Position);

            // The persisted cache stores the requested-series position too, so cache hits stay correct.
            audiobookRepository.Verify(
                repository => repository.UpsertCachedSeriesAsync(It.Is<SeriesCacheEntry>(entry =>
                    entry.CatalogBooks != null &&
                    entry.CatalogBooks.Count == 1 &&
                    entry.CatalogBooks[0].Series == "Mistborn" &&
                    entry.CatalogBooks[0].SeriesNumber == "3")),
                Times.Once);
        }
    }
}
