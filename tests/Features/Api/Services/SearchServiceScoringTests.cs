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
using Listenarr.Application.Interfaces.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Application.Search;
using Listenarr.Application.Metadata;
using Listenarr.Application.Search.Strategies;
using Listenarr.Application.Search.Filters;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SearchServiceScoringTests
    {
        private static SearchService CreateSearchService()
        {
            var client = new System.Net.Http.HttpClient();
            var configuration = Mock.Of<IConfigurationService>();
            var logger = NullLogger<SearchService>.Instance;
            var openLibraryService = Mock.Of<IOpenLibraryService>();
            var imageCache = Mock.Of<IImageCacheService>();
            var audible = new AudibleService(new System.Net.Http.HttpClient(), NullLogger<AudibleService>.Instance);
            var converters = new MetadataConverters(imageCache, NullLogger<MetadataConverters>.Instance);
            var progress = new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance);
            var pipeline = new SearchResultFilterPipeline(Enumerable.Empty<ISearchResultFilter>(), NullLogger<SearchResultFilterPipeline>.Instance);
            var coordinator = new MetadataStrategyCoordinator(Enumerable.Empty<IMetadataStrategy>(), NullLogger<MetadataStrategyCoordinator>.Instance);
            var collector = new AsinCandidateCollector(NullLogger<AsinCandidateCollector>.Instance, openLibraryService, converters, progress);
            var enricher = new AsinEnricher(NullLogger<AsinEnricher>.Instance, coordinator, converters, pipeline, progress);
            var scorer = new SearchResultScorerService(NullLogger<SearchResultScorerService>.Instance);
            var handler = new AsinSearchHandler(NullLogger<AsinSearchHandler>.Instance, configuration, audible, Mock.Of<IAudnexusService>(), converters, progress);

            return new SearchService(
              client,
              configuration,
              logger,
              Mock.Of<IIndexerRepository>(),
              Mock.Of<IApiConfigurationRepository>(),
              audible,
              converters,
              progress,
              collector,
              enricher,
              scorer,
              handler,
              Enumerable.Empty<IIndexerSearchProvider>());
        }

        [Fact]
        public void QualityShouldBeatLargeSeederAdvantage()
        {
            var service = CreateSearchService();

            var flac = new SearchResult
            {
                Title = "Flac Release",
                Quality = "FLAC",
                Seeders = 1,
                Size = 200 * 1024 * 1024,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var mp3HighSeed = new SearchResult
            {
                Title = "MP3 High Seed",
                Quality = "MP3 320kbps",
                Seeders = 5000,
                Size = 200 * 1024 * 1024,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

            var flacScore = service.CalculateProwlarrStyleScore(flac);
            var mp3Score = service.CalculateProwlarrStyleScore(mp3HighSeed);

            Assert.True(flacScore > mp3Score, $"Expected FLAC ({flacScore}) to score higher than MP3 ({mp3Score})");
        }

        [Fact]
        public void UsenetWithGrabsScoresReasonably()
        {
            var service = CreateSearchService();

            var usenet = new SearchResult
            {
                Title = "Usenet release",
                NzbUrl = "http://example.nzb/file.nzb",
                Grabs = 50,
                Quality = null,
                Size = 800 * 1024 * 1024,
                PublishedDate = DateTime.UtcNow.AddDays(-2).ToString("o")
            };

            var torrentLowSeed = new SearchResult
            {
                Title = "Torrent low seed",
                Seeders = 1,
                Quality = null, // missing quality
                Size = 20 * 1024 * 1024, // small suspicious size
                PublishedDate = DateTime.UtcNow.AddYears(-2).ToString("o") // old
            };

            var usenetScore = service.CalculateProwlarrStyleScore(usenet);
            var torrentScore = service.CalculateProwlarrStyleScore(torrentLowSeed);

            Assert.True(usenetScore > torrentScore, $"Expected Usenet ({usenetScore}) to score higher than low-seed torrent ({torrentScore})");
        }
    }
}
