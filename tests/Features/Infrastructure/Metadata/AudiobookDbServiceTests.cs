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
using Listenarr.Infrastructure.Metadata.Providers.AudiobookDb;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Metadata
{
    [Trait("Area", "Metadata")]
    [Trait("Name", nameof(AudiobookDbServiceTests))]
    [Trait("Category", "Infrastructure")]
    public class AudiobookDbServiceTests : BaseTests
    {
        // A trimmed but real response captured from GET
        // /api/audiobooks/external/audible/B082BHJMFF (Andy Weir's "The Martian"), which has
        // two distinct narrated Releases: the original Podium/R.C. Bray recording and the
        // later official Audible Studios/Wil Wheaton re-recording.
        private const string MartianResponseJson = """
        {
          "id": "WQQDcMBQ3qUT",
          "title": "The Martian",
          "releases": [
            {
              "id": "dPL9fC7YnK8X",
              "title": "The Martian",
              "releaseDate": "2013-03-22T00:00:00Z",
              "duration": "10:53:35",
              "publisher": { "id": "znR3ghq6ipmr", "name": "Podium Publishing" },
              "people": [
                { "role": { "name": "Narrator" }, "person": { "id": "HKxn6QeChR9j", "name": "R.C. Bray" } }
              ]
            },
            {
              "id": "RAffFyqchypK",
              "title": "The Martian",
              "releaseDate": "2020-01-01T00:00:00Z",
              "duration": "10:59:29",
              "disambiguation": "unabridged",
              "publisher": { "id": "hf7GRcg8BbXk", "name": "Audible Studios" },
              "people": [
                { "role": { "name": "Narrator" }, "person": { "id": "mBdR4HWfmKJn", "name": "Wil Wheaton" } }
              ]
            }
          ]
        }
        """;

        private sealed class StubHandler(HttpStatusCode statusCode, string? body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(statusCode);
                if (body != null)
                {
                    response.Content = new StringContent(body);
                }

                return Task.FromResult(response);
            }
        }

        private static AudiobookDbService CreateService(HttpStatusCode statusCode, string? body)
        {
            var httpClient = new HttpClient(new StubHandler(statusCode, body));
            return new AudiobookDbService(httpClient, NullLogger<AudiobookDbService>.Instance);
        }

        [Fact]
        public async Task GetBookByAudibleAsinAsync_TwoDistinctReleases_ParsesBothNarrators()
        {
            var service = CreateService(HttpStatusCode.OK, MartianResponseJson);

            var book = await service.GetBookByAudibleAsinAsync("B082BHJMFF");

            Assert.NotNull(book);
            Assert.Equal("The Martian", book!.Title);
            Assert.Equal(2, book.Releases.Count);

            var podium = book.Releases.Single(r => r.PublisherName == "Podium Publishing");
            Assert.Contains("R.C. Bray", podium.Narrators);

            var audible = book.Releases.Single(r => r.PublisherName == "Audible Studios");
            Assert.Contains("Wil Wheaton", audible.Narrators);
            Assert.Equal("unabridged", audible.Disambiguation);
        }

        [Fact]
        public async Task GetBookByAudibleAsinAsync_NotFound_ReturnsNull()
        {
            var service = CreateService(HttpStatusCode.NotFound, null);

            var book = await service.GetBookByAudibleAsinAsync("UNKNOWNASIN");

            Assert.Null(book);
        }

        [Fact]
        public async Task GetBookByAudibleAsinAsync_BlankAsin_ReturnsNullWithoutCallingHttp()
        {
            var service = CreateService(HttpStatusCode.InternalServerError, null);

            var book = await service.GetBookByAudibleAsinAsync("   ");

            Assert.Null(book);
        }
    }
}
