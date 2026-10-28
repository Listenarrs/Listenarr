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
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Application.Search.Filters;
using Listenarr.Application.Search.Strategies;

namespace Listenarr.Tests.Features.Application.Search.Core
{
    public class SearchServiceFixesTests
    {
        [Fact]
        public void ParseMyAnonamouse_With_NoDateOrAge_Sets_Empty_PublishedDate()
        {
            var json = "[ { \"guid\": \"https://www.myanonamouse.net/t/100\", \"size\": 12345, \"title\": \"Test Title\" } ]";
            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var results = MyAnonamouseResponseParser.Parse(json, indexer, NullLogger.Instance);

            Assert.Single(results);
            var r = results[0];
            Assert.True(string.IsNullOrWhiteSpace(r.PublishedDate));
        }

        [Fact]
        public void ParseMyAnonamouse_Always_Sets_Grabs_Even_If_Zero()
        {
            var json = "[ { \"guid\": \"https://www.myanonamouse.net/t/101\", \"grabs\": \"0\", \"files\": \"1\", \"title\": \"Test Title 2\" } ]";
            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var results = MyAnonamouseResponseParser.Parse(json, indexer, NullLogger.Instance);

            Assert.Single(results);
            var r = results[0];
            Assert.Equal(0, r.Grabs);
            Assert.Equal(1, r.Files);
        }

        [Fact]
        public void ToSearchResult_DoesNot_Detect_Language_For_Usenet()
        {
            var idx = new IndexerSearchResult
            {
                Id = "u1",
                Title = "Some Title [ENG] Test",
                Artist = "Author",
                Size = 456,
                Seeders = 0,
                Leechers = 0,
                Quality = "",
                Grabs = 0,
                Files = 0,
                DownloadType = "Usenet",
                Source = "altHUB"
            };

            var sr = Listenarr.Domain.Search.SearchResultConverters.ToSearchResult(idx);
            Assert.Null(sr.Language);
        }

        [Fact]
        public void ToSearchResult_DoesNot_Preserve_Unknown_Language_From_Metadata()
        {
            var md = new MetadataSearchResult
            {
                Id = "m1",
                Title = "Metadata Title",
                Language = "Unknown",
                Source = "Audible",
                PublishYear = "2020"
            };

            var sr = Listenarr.Domain.Search.SearchResultConverters.ToSearchResult(md);
            Assert.Null(sr.Language);
        }

        [Fact]
        public void ToSearchResult_DoesNot_Preserve_Unknown_Quality_From_Indexer()
        {
            var idx = new IndexerSearchResult
            {
                Id = "i1",
                Title = "Quality Test",
                Size = 1000,
                Seeders = 10,
                Leechers = 2,
                Quality = "Unknown",
                Grabs = 0,
                Files = 0,
                DownloadType = "Torrent",
                Source = "test"
            };

            var sr = Listenarr.Domain.Search.SearchResultConverters.ToSearchResult(idx);
            Assert.Null(sr.Quality);
        }

        [Fact]
        public async Task SearchByAsinAsync_Uses_Requested_Region_For_Audible_Source_Links()
        {
            var configuration = Mock.Of<IConfigurationService>();
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance);
            audible
                .Setup(s => s.GetBookMetadataAsync("B0TEST1234", "de", true, "german"))
                .ReturnsAsync(new AudibleBookResponse
                {
                    Asin = "B0TEST1234",
                    Title = "Region Test",
                    Authors = new List<AudibleAuthor> { new() { Name = "Test Author", Region = "de" } },
                    Language = "german"
                });

            var converters = new MetadataConverters(Mock.Of<IImageCacheService>(), NullLogger<MetadataConverters>.Instance);
            var progress = new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance);
            var handler = new AsinSearchHandler(
                NullLogger<AsinSearchHandler>.Instance,
                configuration,
                audible.Object,
                Mock.Of<IAudnexusService>(),
                converters,
                progress);

            var results = await handler.SearchByAsinAsync(
                "B0TEST1234",
                new List<ApiConfiguration>(),
                region: "de",
                language: "german");

            var result = Assert.Single(results);
            Assert.Equal("https://www.audible.de/pd/B0TEST1234", result.SourceLink);
            Assert.Equal("https://www.audible.de/pd/B0TEST1234", result.ProductUrl);
            audible.Verify(s => s.GetBookMetadataAsync("B0TEST1234", "de", true, "german"), Times.Once);
        }

        [Fact]
        public async Task AudibleMetadataStrategy_Uses_Requested_Region_For_Metadata_Lookup()
        {
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance);
            audible
                .Setup(s => s.GetBookMetadataAsync("B0STRATEGY", "de", true, null))
                .ReturnsAsync(new AudibleBookResponse
                {
                    Asin = "B0STRATEGY",
                    Title = "Strategy Test",
                    Authors = new List<AudibleAuthor> { new() { Name = "Test Author" } }
                });

            var strategy = new AudibleMetadataStrategy(
                audible.Object,
                new MetadataConverters(Mock.Of<IImageCacheService>(), NullLogger<MetadataConverters>.Instance),
                NullLogger<AudibleMetadataStrategy>.Instance);

            var metadata = await strategy.FetchMetadataAsync(
                "B0STRATEGY",
                new ApiConfiguration { Name = "Audible", BaseUrl = "https://api.audible.com" },
                "Audible",
                "de");

            Assert.NotNull(metadata);
            audible.Verify(s => s.GetBookMetadataAsync("B0STRATEGY", "de", true, null), Times.Once);
            audible.Verify(s => s.GetBookMetadataAsync("B0STRATEGY", "us", It.IsAny<bool>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task AudnexusStrategy_Uses_Requested_Region_For_Metadata_Lookup()
        {
            var audnexus = new Mock<IAudnexusService>();
            audnexus
                .Setup(s => s.GetBookMetadataAsync("B0AUDNEXUS", "de", true, false))
                .ReturnsAsync(new AudnexusBookResponse
                {
                    Asin = "B0AUDNEXUS",
                    Title = "Audnexus Strategy Test",
                    Authors = new List<AudnexusAuthor> { new() { Name = "Test Author" } }
                });

            var strategy = new AudnexusStrategy(
                audnexus.Object,
                new MetadataConverters(Mock.Of<IImageCacheService>(), NullLogger<MetadataConverters>.Instance),
                NullLogger<AudnexusStrategy>.Instance);

            var metadata = await strategy.FetchMetadataAsync(
                "B0AUDNEXUS",
                new ApiConfiguration { Name = "Audnexus", BaseUrl = "https://api.audnex.us" },
                "Audible",
                "de");

            Assert.NotNull(metadata);
            audnexus.Verify(s => s.GetBookMetadataAsync("B0AUDNEXUS", "de", true, false), Times.Once);
            audnexus.Verify(s => s.GetBookMetadataAsync("B0AUDNEXUS", "us", It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task EnrichAsinsAsync_Uses_Requested_Region_When_Metadata_Response_Omits_Region()
        {
            var strategy = new RegionCapturingMetadataStrategy();
            var coordinator = new MetadataStrategyCoordinator(
                new IMetadataStrategy[] { strategy },
                NullLogger<MetadataStrategyCoordinator>.Instance);
            var enricher = new AsinEnricher(
                NullLogger<AsinEnricher>.Instance,
                coordinator,
                new MetadataConverters(Mock.Of<IImageCacheService>(), NullLogger<MetadataConverters>.Instance),
                new SearchResultFilterPipeline(Enumerable.Empty<ISearchResultFilter>(), NullLogger<SearchResultFilterPipeline>.Instance),
                new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance));

            var result = await enricher.EnrichAsinsAsync(
                new List<string> { "B0ENRICHED" },
                new Dictionary<string, (string Title, string Author, string? ImageUrl, string? Language)>
                {
                    ["B0ENRICHED"] = ("Enriched Region Test", "Test Author", null, "german")
                },
                new Dictionary<string, string> { ["B0ENRICHED"] = "Audible" },
                new Dictionary<string, OpenLibraryBook>(),
                new List<ApiConfiguration>
                {
                    new() { Name = "Audible", BaseUrl = "https://api.audible.com", IsEnabled = true }
                },
                query: "Enriched Region Test",
                region: "de");

            var enriched = Assert.Single(result.EnrichedResults);
            Assert.Equal("de", strategy.CapturedRegion);
            Assert.Equal("https://www.audible.de/pd/B0ENRICHED", enriched.ProductUrl);
            Assert.Equal("https://www.audible.de/pd/B0ENRICHED", enriched.SourceLink);
        }

        private sealed class RegionCapturingMetadataStrategy : IMetadataStrategy
        {
            public string SourceName => "Audible";
            public string? CapturedRegion { get; private set; }

            public bool CanHandle(ApiConfiguration source) => true;

            public Task<AudibleBookMetadata?> FetchMetadataAsync(
                string asin,
                ApiConfiguration source,
                string? originalSource,
                string? region = null)
            {
                CapturedRegion = region;
                return Task.FromResult<AudibleBookMetadata?>(new AudibleBookMetadata
                {
                    Asin = asin,
                    Source = originalSource ?? "Audible",
                    Title = "Enriched Region Test",
                    Authors = new List<string> { "Test Author" },
                    Language = "german"
                });
            }
        }
    }
}
