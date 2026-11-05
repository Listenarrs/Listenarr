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
 */
using System.Net;
using System.Text;
using Listenarr.Api.Features.Metadata;
using Listenarr.Infrastructure.Metadata.Providers.Audiosilo;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Metadata
{
    [Trait("Area", "Metadata")]
    [Trait("Name", nameof(AudiosiloMetaServiceTests))]
    [Trait("Category", "Infrastructure")]
    public class AudiosiloMetaServiceTests : BaseTests
    {
        // Shapes captured from https://meta.audiosilo.app/api/v1 (trimmed to the fields used).
        private const string SearchJson = """
        {"results": [
          {"kind": "work", "id": "guards-guards", "title": "Guards! Guards!",
           "authors": [{"id": "terry-pratchett", "name": "Terry Pratchett"}]},
          {"kind": "work", "id": "guards-guards-parody", "title": "Guards! Guards!",
           "authors": [{"id": "someone-else", "name": "Someone Else"}]},
          {"kind": "person", "id": "terry-pratchett", "title": "Terry Pratchett"}
        ]}
        """;

        private const string WorkJson = """
        {"id": "guards-guards", "title": "Guards! Guards!", "language": "en",
         "authors": [{"id": "terry-pratchett", "name": "Terry Pratchett"}],
         "series": {"id": "discworld", "name": "Discworld", "position": "8"},
         "recordings": [
           {"id": "r1", "narrators": [{"name": "Ben Aaronovitch"}, {"name": "Jon Culshaw"}], "runtime_min": 809,
            "publisher": "Transworld Digital", "release_date": "2023-05-25",
            "asin": [{"region": "de", "asin": "B09M8WBB75"}, {"region": "uk", "asin": "B09M8W9JY5"}]},
           {"id": "r2", "narrators": [{"name": "Nigel Planer"}], "runtime_min": 608,
            "publisher": "Random House AudioBooks", "release_date": "2007-07-12", "cover_url": "https://example/c.jpg",
            "asin": [{"region": "uk", "asin": "b004fu8xpa"}]},
           {"id": "r3", "narrators": [{"name": "Tony Robinson"}], "runtime_min": 194, "abridged": true,
            "asin": []}
         ]}
        """;

        private sealed class StubHandler : HttpMessageHandler
        {
            public int Calls { get; private set; }
            public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                var path = request.RequestUri!.AbsolutePath;
                var body = path.EndsWith("/search", StringComparison.Ordinal) ? SearchJson
                    : path.EndsWith("/works/guards-guards", StringComparison.Ordinal) ? WorkJson
                    : "{}";
                return Task.FromResult(new HttpResponseMessage(Status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }
        }

        private static AudiosiloMetaService CreateService(StubHandler handler) =>
            new(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<AudiosiloMetaService>.Instance);

        [Fact]
        public async Task SearchEditionsAsync_ReturnsEveryRecordingOfTheMatchingWork()
        {
            var service = CreateService(new StubHandler());

            var editions = await service.SearchEditionsAsync("Guards! Guards!", "Terry Pratchett");

            Assert.Equal(3, editions.Count);
            var planer = Assert.Single(editions, e => e.Narrators.Contains("Nigel Planer"));
            Assert.Equal(608, planer.RuntimeMinutes);
            Assert.Equal("Random House AudioBooks", planer.Publisher);
            Assert.Equal(new RegionalAsin("uk", "B004FU8XPA"), Assert.Single(planer.Asins));
            Assert.Equal("Discworld", planer.Series);
            Assert.Equal("8", planer.SeriesPosition);
            Assert.Equal(new[] { "Terry Pratchett" }, planer.Authors);
            Assert.True(Assert.Single(editions, e => e.RuntimeMinutes == 194).Abridged);
        }

        [Fact]
        public async Task SearchEditionsAsync_FiltersOutWorksByOtherAuthors()
        {
            var handler = new StubHandler();
            var service = CreateService(handler);

            await service.SearchEditionsAsync("Guards! Guards!", "Terry Pratchett");

            // One search call plus one work call: the other author's same-titled work is not fetched.
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task SearchEditionsAsync_ReturnsEmpty_WhenTheServiceFails()
        {
            var service = CreateService(new StubHandler { Status = HttpStatusCode.ServiceUnavailable });

            var editions = await service.SearchEditionsAsync("Guards! Guards!", "Terry Pratchett");

            Assert.Empty(editions);
        }

        [Fact]
        public async Task SearchEditionsAsync_CachesRepeatedLookups()
        {
            var handler = new StubHandler();
            var service = CreateService(handler);

            await service.SearchEditionsAsync("Guards! Guards!", "Terry Pratchett");
            await service.SearchEditionsAsync("guards guards", "TERRY PRATCHETT");

            Assert.Equal(2, handler.Calls);
        }

        [Theory]
        [InlineData("Guards! Guards!", "guards guards", true)]
        [InlineData("Harry Potter and the Chamber of Secrets", "Harry Potter and the Chamber", true)]
        [InlineData("Mort", "Mortal Engines", false)]
        [InlineData("Eric", "Eric", true)]
        public void TitlesMatch_ComparesNormalisedTitles(string candidate, string wanted, bool expected)
        {
            Assert.Equal(expected, AudiosiloMetaService.TitlesMatch(candidate, wanted));
        }

        [Fact]
        public void EditionSearchController_PrefersTheAsinOfTheRequestedRegion()
        {
            var edition = new AudiobookEditionCandidate(
                "Audiosilo", "guards-guards", "Guards! Guards!", new[] { "Terry Pratchett" }, new[] { "Jon Culshaw" },
                809, "Transworld Digital", "2023-05-25",
                new[] { new RegionalAsin("de", "B09M8WBB75"), new RegionalAsin("uk", "B09M8W9JY5") },
                null, "Discworld", "8", "en", null);

            var uk = EditionSearchController.ToSearchResult(edition, "uk");
            var fr = EditionSearchController.ToSearchResult(edition, "fr");

            Assert.Equal(("B09M8W9JY5", "uk"), (uk.Asin, uk.Region));
            Assert.Equal(("B09M8WBB75", "de"), (fr.Asin, fr.Region));
            Assert.Equal(809, uk.LengthMinutes);
            Assert.Equal("Discworld", Assert.Single(uk.Series).Name);
            Assert.Equal("Audiosilo", uk.Source);
        }

        [Fact]
        public async Task EditionSearchController_RequiresATitle()
        {
            var controller = new EditionSearchController(CreateService(new StubHandler()));

            var result = await controller.Search(" ", null, null);

            Assert.IsType<BadRequestObjectResult>(result);
        }
    }
}
