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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Search
{
    [Trait("Area", "SearchApi")]
    [Trait("Name", "SearchControllerUnifiedTests")]
    [Trait("Category", "SearchController")]
    public class SearchControllerUnifiedTests : BaseTests
    {
        private SearchController CreateController(
            Mock<ISearchService>? searchService = null,
            StubAudibleService? audibleService = null,
            Mock<IAudiobookMetadataService>? metadataService = null,
            Mock<IConfigurationService>? configurationService = null,
            Action<ServiceCollectionBuilder>? configureServices = null)
        {
            searchService ??= new Mock<ISearchService>();
            audibleService ??= new StubAudibleService();
            metadataService ??= new Mock<IAudiobookMetadataService>();

            Init(services =>
            {
                services
                    .Without<IImageCacheService>()
                    .Without<MetadataConverters>()
                    .WithSingleton<ISearchService>(searchService.Object)
                    .WithSingleton<AudibleService>(audibleService)
                    .WithSingleton<IAudiobookMetadataService>(metadataService.Object)
                    .WithTransient<SearchController, SearchController>();

                if (configurationService != null)
                {
                    services.WithSingleton<IConfigurationService>(configurationService.Object);
                }

                configureServices?.Invoke(services);
            });

            var controller = _provider.GetRequiredService<SearchController>();
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        [Fact]
        public async Task AdvancedSearch_TitleOnly_Uses_Audible_SearchByTitleAsync()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            var sample = new AudibleSearchResponse
            {
                Results = new List<AudibleSearchResult>
                {
                    new AudibleSearchResult { Asin = "BTEST1", Title = "T" }
                },
                TotalResults = 1
            };

            stubAudible.ResponseToReturn = sample;
            mockMeta.Setup(m => m.GetAudibleMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(new AudibleBookResponse { Asin = "BTEST1", Title = "T" });

            var controller = CreateController(mockSearch, stubAudible, mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Advanced, Title = "T", Pagination = new Pagination { Page = 1, Limit = 10 } };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            // Advanced requests are routed through the unified IntelligentSearch pipeline
            mockSearch.Verify(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AdvancedSearch_TitleAndAuthor_Uses_AuthorFlow()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible2 = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            var sample = new AudibleSearchResponse
            {
                Results = new List<AudibleSearchResult>
                {
                    new AudibleSearchResult { Asin = "BAUTH1", Title = "Title" }
                },
                TotalResults = 1
            };

            stubAudible2.ResponseToReturn = sample;
            mockMeta.Setup(m => m.GetAudibleMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(new AudibleBookResponse { Asin = "BAUTH1", Title = "Title" });

            var controller = CreateController(mockSearch, stubAudible2, mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Advanced, Title = "Title", Author = "Author", Pagination = new Pagination { Page = 1, Limit = 20 } };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            // Author+Title advanced searches are processed by the intelligent search pipeline
            mockSearch.Verify(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AdvancedSearch_IsbnOnly_Uses_SearchByIsbnAsync()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible3 = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            var sample = new AudibleSearchResponse
            {
                Results = new List<AudibleSearchResult>
                {
                    new AudibleSearchResult { Asin = "BISBN1", Title = "ISBNTitle" }
                },
                TotalResults = 1
            };

            stubAudible3.ResponseToReturn = sample;

            var controller = CreateController(mockSearch, stubAudible3, mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Advanced, Isbn = "9780000000", Pagination = new Pagination { Page = 1, Limit = 10 } };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            // ISBN advanced searches are routed through the unified intelligent search pipeline
            mockSearch.Verify(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AdvancedSearch_AsinOnly_Uses_GetBookMetadataAsync()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible4 = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            stubAudible4.BookResponseToReturn = new AudibleBookResponse { Asin = "BASIN", Title = "ASIN Title" };

            var controller = CreateController(mockSearch, stubAudible4, mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Advanced, Asin = "BASIN", Region = "de", Language = "german" };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            Assert.Equal("GetBookMetadataAsync", stubAudible4.LastMethod);
            Assert.Equal("BASIN", stubAudible4.LastTitle);
            Assert.Equal("de", stubAudible4.LastRegion);
            Assert.Equal("german", stubAudible4.LastLanguage);
        }

        [Fact]
        public async Task SimpleSearch_WithoutRegion_Uses_ConfiguredDefaultRegion()
        {
            var mockSearch = new Mock<ISearchService>();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                      .ReturnsAsync(new List<MetadataSearchResult>());
            var mockMeta = new Mock<IAudiobookMetadataService>();
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                      .ReturnsAsync(new ApplicationSettings { DefaultSearchRegion = "de" });

            var controller = CreateController(mockSearch, metadataService: mockMeta, configurationService: mockConfig);

            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(new { mode = "simple", query = "Dune" });

            await controller.Search(reqJson);

            mockSearch.Verify(s => s.IntelligentSearchAsync("Dune", 50, 50, "Relaxed", false, 0.7, "de", null, It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AdvancedSearch_WithoutRegion_Uses_ConfiguredDefaultRegion()
        {
            var mockSearch = new Mock<ISearchService>();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                      .ReturnsAsync(new List<MetadataSearchResult>());
            var mockMeta = new Mock<IAudiobookMetadataService>();
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                      .ReturnsAsync(new ApplicationSettings { DefaultSearchRegion = "fr" });

            var controller = CreateController(mockSearch, metadataService: mockMeta, configurationService: mockConfig);

            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(new { mode = "advanced", title = "Dune" });

            await controller.Search(reqJson);

            mockSearch.Verify(s => s.IntelligentSearchAsync(It.IsAny<string>(), 200, 50, "Relaxed", false, 0.7, "fr", null, It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AdvancedSearch_AsinWithoutRegion_Uses_ConfiguredDefaultRegion()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible = new StubAudibleService
            {
                BookResponseToReturn = new AudibleBookResponse { Asin = "BASIN", Title = "ASIN Title" }
            };
            var mockMeta = new Mock<IAudiobookMetadataService>();
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                      .ReturnsAsync(new ApplicationSettings { DefaultSearchRegion = "de" });

            var controller = CreateController(mockSearch, stubAudible, mockMeta, mockConfig);

            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(new { mode = "advanced", asin = "BASIN" });

            await controller.Search(reqJson);

            Assert.Equal("GetBookMetadataAsync", stubAudible.LastMethod);
            Assert.Equal("de", stubAudible.LastRegion);
        }

        [Fact]
        public async Task IntelligentSearch_WithoutRegion_Uses_ConfiguredDefaultRegion()
        {
            var mockSearch = new Mock<ISearchService>();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                      .ReturnsAsync(new List<MetadataSearchResult>());
            var mockMeta = new Mock<IAudiobookMetadataService>();
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                      .ReturnsAsync(new ApplicationSettings { DefaultSearchRegion = "jp" });

            var controller = CreateController(mockSearch, metadataService: mockMeta, configurationService: mockConfig);

            await controller.IntelligentSearch("Dune");

            mockSearch.Verify(s => s.IntelligentSearchAsync("Dune", 50, 50, "Relaxed", false, 0.7, "jp", null, It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "SearchByTitle")]
        [Trait("Scenario", "UsesConfiguredDefaultRegionForTitleFallback")]
        public async Task SearchByTitle_WithoutRegion_Uses_ConfiguredDefaultRegion_And_RegionalSourceUrl()
        {
            // Given
            var mockSearch = new Mock<ISearchService>();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                      .ReturnsAsync(new List<MetadataSearchResult>
                      {
                          new MetadataSearchResultBuilder()
                              .WithAsin("B0DUNE1234")
                              .WithTitle("Dune")
                              .WithArtist("Frank Herbert")
                              .Build()
                      });
            var mockMeta = new Mock<IAudiobookMetadataService>();
            mockMeta.Setup(m => m.GetAudibleMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                    .ReturnsAsync((AudibleBookResponse?)null);
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                      .ReturnsAsync(new ApplicationSettings { DefaultSearchRegion = "de" });

            var controller = CreateController(mockSearch, metadataService: mockMeta, configurationService: mockConfig);

            // When
            var response = await controller.SearchByTitle("TITLE:Dune");

            // Then
            var ok = Assert.IsType<OkObjectResult>(response.Result);
            var serialized = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            using var doc = System.Text.Json.JsonDocument.Parse(serialized);
            var result = Assert.Single(doc.RootElement.EnumerateArray());
            Assert.Equal("https://www.audible.de", result.GetProperty("sourceUrl").GetString());
            mockSearch.Verify(s => s.IntelligentSearchAsync("TITLE:Dune", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), "de", null, It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Method", "SearchByTitle")]
        [Trait("Scenario", "UsesConfiguredDefaultRegionForAsinShortCircuit")]
        public async Task SearchByTitle_AsinWithoutRegion_Uses_ConfiguredDefaultRegion_And_RegionalSourceUrl()
        {
            // Given
            var mockSearch = new Mock<ISearchService>();
            var stubAudible = new StubAudibleService
            {
                BookResponseToReturn = new AudibleBookResponse { Asin = "B0TEST1234", Title = "Localized Result" }
            };
            var mockMeta = new Mock<IAudiobookMetadataService>();
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync())
                      .ReturnsAsync(new ApplicationSettings { DefaultSearchRegion = "br" });

            var controller = CreateController(mockSearch, stubAudible, mockMeta, mockConfig);

            // When
            var response = await controller.SearchByTitle("B0TEST1234");

            // Then
            var ok = Assert.IsType<OkObjectResult>(response.Result);
            var serialized = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            using var doc = System.Text.Json.JsonDocument.Parse(serialized);
            var result = Assert.Single(doc.RootElement.EnumerateArray());
            Assert.Equal("https://www.audible.com.br", result.GetProperty("sourceUrl").GetString());
            Assert.Equal("br", stubAudible.LastRegion);
            mockSearch.Verify(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task AdvancedSearch_SeriesName_With_Asin_Property_Uses_SeriesAsin()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            // Simulate series search returning SeriesLookupItem list with ASIN
            stubAudible.SeriesResponseToReturn = new List<SeriesLookupItem>
            {
                new() { Asin = "B0SERIES1234", Name = "Some Series", Region = "us" }
            };

            var controller = CreateController(mockSearch, stubAudible, mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Advanced, Title = "Title", Series = "Some Series" };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            // Series-only search should resolve an ASIN and fetch books for that ASIN
            Assert.Equal("GetBooksBySeriesAsinAsync", stubAudible.LastMethod);
            Assert.Equal("B0SERIES1234", stubAudible.LastSeriesAsin);
        }

        [Fact]
        public async Task AdvancedSearch_SeriesName_With_NonMatching_Region_Falls_Back_To_First_Asin()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            // Simulate series search returning items whose region doesn't match the request —
            // the code should still pick the first item with a valid ASIN as a fallback
            stubAudible.SeriesResponseToReturn = new List<SeriesLookupItem>
            {
                new() { Asin = "B0FALLBACK123", Name = "Some Series", Region = "de" }
            };

            var controller = CreateController(mockSearch, stubAudible, mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Advanced, Title = "Title", Series = "Some Series" };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            // Series-only search should resolve an ASIN and fetch books for that ASIN
            Assert.Equal("GetBooksBySeriesAsinAsync", stubAudible.LastMethod);
            Assert.Equal("B0FALLBACK123", stubAudible.LastSeriesAsin);
        }

        [Fact]
        public async Task AdvancedSearch_AuthorAndSeries_Uses_AuthorFlow_And_Filters_By_Series()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            // Simulate IntelligentSearch returning two metadata records, only one in the requested series
            var md1 = new MetadataSearchResultBuilder()
                .WithAsin("B1")
                .WithTitle("Book One")
                .WithSeries("Target Series")
                .Build();
            var md2 = new MetadataSearchResultBuilder()
                .WithAsin("B2")
                .WithTitle("Book Two")
                .WithSeries("Other Series")
                .Build();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                      .ReturnsAsync(new List<MetadataSearchResult> { md1, md2 });

            var controller = CreateController(mockSearch, stubAudible, mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Advanced, Author = "Some Author", Series = "Target Series" };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            // Ensure the author flow (intelligent search) was used
            mockSearch.Verify(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()), Times.Once);

            // Validate returned results were filtered by series (response is { results: [...], totalResults: N })
            var ok = Assert.IsType<OkObjectResult>(res.Result);
            var serialized = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            using var doc = System.Text.Json.JsonDocument.Parse(serialized);
            var root = doc.RootElement;
            var resultsEl = root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("results", out var rr) ? rr : root;
            Assert.Equal(System.Text.Json.JsonValueKind.Array, resultsEl.ValueKind);
            Assert.Equal(1, resultsEl.GetArrayLength());
            var first = resultsEl[0];
            Assert.True(first.TryGetProperty("asin", out var asinProp));
            Assert.Equal("B1", asinProp.GetString());
        }

        [Fact]
        public async Task SimpleSearch_Returns_Rich_Audible_When_MetadataAvailable()
        {
            var mockSearch = new Mock<ISearchService>();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            var md = new MetadataSearchResultBuilder()
                .WithAsin("BAUD1")
                .WithTitle("Title")
                .WithProductUrl("https://www.amazon.com/dp/BAUD1")
                .WithEnriched()
                .Build();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                      .ReturnsAsync(new List<MetadataSearchResult> { md });

            var audResp = new AudibleBookResponse
            {
                Asin = "BAUD1",
                Title = "Title",
                Authors = new List<AudibleAuthor> { new AudibleAuthor { Asin = "A1", Name = "Author Name", Region = "us" } },
                Narrators = new List<AudibleNarrator> { new AudibleNarrator { Name = "Narrator Name" } },
                Genres = new List<AudibleGenre> { new AudibleGenre { Asin = "G1", Name = "Fiction", Type = "Fiction" } },
                Series = new List<AudibleSeries> { new AudibleSeries { Asin = "S1", Name = "Series Name", Position = "1" } },
                Region = "de",
                ImageUrl = "http://example.com/cover.jpg",
                LengthMinutes = 600,
                ReleaseDate = "2021-05-04T00:00:00.000Z",
                Explicit = false
            };

            mockMeta.Setup(m => m.GetAudibleMetadataAsync("BAUD1", It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(audResp);

            var controller = CreateController(mockSearch, metadataService: mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Simple, Query = "q", Region = "de" };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            var ok = Assert.IsType<OkObjectResult>(res.Result);
            var serialized = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(serialized);
            Assert.NotNull(parsed);
            Assert.Single(parsed);
            var first = parsed![0];

            Assert.True(first.TryGetProperty("authors", out var aProp));
            var authors = aProp.EnumerateArray();
            var firstAuthor = authors.First();
            Assert.Equal("Author Name", firstAuthor.GetProperty("name").GetString());
            Assert.Equal("A1", firstAuthor.GetProperty("asin").GetString());

            Assert.True(first.TryGetProperty("genres", out var gProp));
            var genres = gProp.EnumerateArray();
            var firstGenre = genres.First();
            Assert.Equal("G1", firstGenre.GetProperty("asin").GetString());

            Assert.True(first.TryGetProperty("series", out var sProp));
            var series = sProp.EnumerateArray();
            var firstSeries = series.First();
            Assert.Equal("S1", firstSeries.GetProperty("asin").GetString());
            Assert.Equal("https://www.audible.de/pd/BAUD1", first.GetProperty("link").GetString());
        }

        [Fact]
        public async Task AdvancedSearch_SeriesFilter_Returns_Empty_When_No_Match()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            // IntelligentSearch returns results whose Series does NOT match the requested series
            var md1 = new MetadataSearchResultBuilder()
                .WithAsin("B1")
                .WithTitle("Unrelated Book")
                .WithSeries("Wrong Series")
                .Build();
            var md2 = new MetadataSearchResultBuilder()
                .WithAsin("B2")
                .WithTitle("Another Unrelated")
                .WithSeries("Also Wrong")
                .Build();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                      .ReturnsAsync(new List<MetadataSearchResult> { md1, md2 });

            var controller = CreateController(mockSearch, stubAudible, mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Advanced, Author = "Some Author", Series = "Dune" };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            var ok = Assert.IsType<OkObjectResult>(res.Result);
            var serialized = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            using var doc = System.Text.Json.JsonDocument.Parse(serialized);
            var root = doc.RootElement;
            var resultsEl = root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("results", out var rr) ? rr : root;
            Assert.Equal(System.Text.Json.JsonValueKind.Array, resultsEl.ValueKind);
            // Should be empty — NOT the unfiltered unrelated results
            Assert.Equal(0, resultsEl.GetArrayLength());
        }

        [Fact]
        public async Task AdvancedSearch_SeriesBooks_With_NullLanguage_Are_Preserved()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            // Simulate series lookup returning a series ASIN
            stubAudible.SeriesResponseToReturn = new List<SeriesLookupItem>
            {
                new() { Asin = "B0DUNE", Name = "Dune", Region = "us" }
            };

            // Override GetBooksBySeriesAsinAsync to return books with null Language
            stubAudible.SeriesBooksOverride = new List<AudibleSearchResult>
            {
                new AudibleSearchResult { Asin = "BDUNE1", Title = "Dune", Language = null },
                new AudibleSearchResult { Asin = "BDUNE2", Title = "Dune Messiah", Language = "English" }
            };

            mockMeta.Setup(m => m.GetAudibleMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                    .ReturnsAsync((string asin, string region, bool force) => new AudibleBookResponse { Asin = asin, Title = "Test" });

            var controller = CreateController(mockSearch, stubAudible, mockMeta);

            // Search with language=english — books with null Language should still be included
            var req = new SearchRequest { Mode = SearchMode.Advanced, Series = "Dune", Region = "us", Language = "english" };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            var ok = Assert.IsType<OkObjectResult>(res.Result);
            var serialized = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            using var arrDoc = System.Text.Json.JsonDocument.Parse(serialized);
            var arr = arrDoc.RootElement;
            Assert.Equal(System.Text.Json.JsonValueKind.Array, arr.ValueKind);
            // Both books should be present — the null-language one is NOT filtered out
            Assert.Equal(2, arr.GetArrayLength());
        }

        [Fact]
        public async Task AdvancedSearch_SeriesOnly_Returns_Books_From_Series_Lookup()
        {
            var mockSearch = new Mock<ISearchService>();
            var stubAudible = new StubAudibleService();
            var mockMeta = new Mock<IAudiobookMetadataService>();

            // Simulate series lookup
            stubAudible.SeriesResponseToReturn = new List<SeriesLookupItem>
            {
                new() { Asin = "B0SERIES", Name = "Test Series", Region = "us" }
            };

            mockMeta.Setup(m => m.GetAudibleMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
                    .ReturnsAsync((string asin, string region, bool force) => new AudibleBookResponse { Asin = asin, Title = "Book in series" });

            var controller = CreateController(mockSearch, stubAudible, mockMeta);

            var req = new SearchRequest { Mode = SearchMode.Advanced, Series = "Test Series", Region = "us" };
            var reqJson = System.Text.Json.JsonSerializer.SerializeToElement(req);
            var res = await controller.Search(reqJson);

            Assert.NotNull(res);
            // Should have called GetBooksBySeriesAsinAsync
            Assert.Equal("GetBooksBySeriesAsinAsync", stubAudible.LastMethod);
            Assert.Equal("B0SERIES", stubAudible.LastSeriesAsin);

            var ok = Assert.IsType<OkObjectResult>(res.Result);
            var serialized = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            using var arrDoc = System.Text.Json.JsonDocument.Parse(serialized);
            var arr = arrDoc.RootElement;
            Assert.Equal(System.Text.Json.JsonValueKind.Array, arr.ValueKind);
            Assert.True(arr.GetArrayLength() > 0, "Series-only search should return at least one book");
        }
    }

    internal class StubAudibleService : AudibleService
    {
        public string? LastMethod { get; set; }
        public string? LastTitle { get; set; }
        public string? LastAuthor { get; set; }
        public string? LastRegion { get; set; }
        public string? LastLanguage { get; set; }
        public int LastPage { get; set; }
        public int LastLimit { get; set; }
        public AudibleSearchResponse? ResponseToReturn { get; set; }
        public AudibleBookResponse? BookResponseToReturn { get; set; }

        public object? SeriesResponseToReturn { get; set; }
        public string? LastSeriesAsin { get; set; }
        public List<AudibleSearchResult>? SeriesBooksOverride { get; set; }

        public StubAudibleService() : base(new HttpClient(), new NullLogger<AudibleService>()) { }

        public override Task<AudibleSearchResponse?> SearchByTitleAsync(string title, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            LastMethod = "SearchByTitleAsync";
            LastTitle = title;
            LastPage = page;
            LastLimit = limit;
            return Task.FromResult(ResponseToReturn);
        }

        public override Task<object?> SearchSeriesByNameAsync(string name, string region = "us")
        {
            LastMethod = "SearchSeriesByNameAsync";
            LastTitle = name;
            return Task.FromResult(SeriesResponseToReturn);
        }

        public override Task<object?> GetBooksBySeriesAsinAsync(string seriesAsin, string region = "us")
        {
            LastMethod = "GetBooksBySeriesAsinAsync";
            LastSeriesAsin = seriesAsin;
            // Return List<AudibleSearchResult> directly — controller casts with "as List<AudibleSearchResult>"
            var books = SeriesBooksOverride ?? new List<AudibleSearchResult>
            {
                new AudibleSearchResult { Asin = seriesAsin, Title = "Book in series" }
            };
            return Task.FromResult<object?>(books);
        }

        public override Task<AudibleSearchResponse?> SearchByTitleAndAuthorPagedAsync(string title, string author, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            LastMethod = "SearchByTitleAndAuthorPagedAsync";
            LastTitle = title;
            LastAuthor = author;
            LastPage = page;
            LastLimit = limit;
            return Task.FromResult(ResponseToReturn);
        }

        public override Task<AudibleSearchResponse?> SearchByIsbnAsync(string isbn, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            LastMethod = "SearchByIsbnAsync";
            LastTitle = isbn;
            LastPage = page;
            LastLimit = limit;
            return Task.FromResult(ResponseToReturn);
        }

        public override Task<AudibleBookResponse?> GetBookMetadataAsync(string asin, string region = "us", bool useCache = true, string? language = null)
        {
            LastMethod = "GetBookMetadataAsync";
            LastTitle = asin;
            LastRegion = region;
            LastLanguage = language;
            return Task.FromResult(BookResponseToReturn);
        }
    }
}
