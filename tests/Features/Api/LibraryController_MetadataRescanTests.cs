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
using System.Net;
using System.Text.Json;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Tests.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api
{
    public class LibraryController_MetadataRescanTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public LibraryController_MetadataRescanTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task RescanMetadata_UsesIdentifiersAndUpdatesAudiobook()
        {
            var metadataMock = new Mock<IAudiobookMetadataService>();
            metadataMock
                .Setup(m => m.GetMetadataAsync("B0TESTASIN", "us", false))
                .ReturnsAsync(new
                {
                    metadata = new AudibleBookResponse
                    {
                        Asin = "B0TESTASIN",
                        Title = "Fixed Metadata Title",
                        Subtitle = "Recovered Subtitle",
                        Authors = new List<AudibleAuthor> { new() { Name = "Correct Author" } },
                        Narrators = new List<AudibleNarrator> { new() { Name = "Correct Narrator" } },
                        Publisher = "Test Publisher",
                        Description = "<p>Recovered description</p>",
                        Genres = new List<AudibleGenre>
                        {
                            new() { Name = "Fantasy" },
                            new() { Name = "Epic Fantasy" }
                        },
                        ReleaseDate = "2024-01-05T00:00:00.000Z",
                        LengthMinutes = 615,
                        Language = "English",
                        Isbn = "9781234567897"
                    },
                    source = "Audible",
                    sourceUrl = "https://audible.de"
                });

            var asinLookupMock = new Mock<IAsinLookupService>();

            var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAudiobookMetadataService>();
                    services.AddSingleton(metadataMock.Object);

                    services.RemoveAll<IAsinLookupService>();
                    services.AddSingleton(asinLookupMock.Object);
                });
            });

            int audiobookId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var audiobook = new Audiobook
                {
                    Title = "Broken Title",
                    Authors = new List<string> { "Wrong Author" },
                    Monitored = true,
                    Asin = "B0TESTASIN",
                    ExternalIdentifiers = new List<AudiobookExternalIdentifier>
                    {
                        new()
                        {
                            Type = AudiobookExternalIdentifierType.Asin,
                            ValueRaw = "B0TESTASIN",
                            ValueNormalized = "B0TESTASIN",
                            Region = "us",
                            IsPrimary = true,
                            Source = AudiobookExternalIdentifierSource.Manual
                        }
                    }
                };

                db.Audiobooks.Add(audiobook);
                await db.SaveChangesAsync();
                audiobookId = audiobook.Id;
            }

            var client = factory.CreateClient();
            var response = await PostRescanAsync(client, audiobookId);
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.IsSuccessStatusCode,
                $"Expected success but got {(int)response.StatusCode} {response.StatusCode}: {responseBody}");

            await using (var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(responseBody)))
            using (var json = await JsonDocument.ParseAsync(stream))
            {
                Assert.Equal("Metadata rescanned successfully", json.RootElement.GetProperty("message").GetString());
                Assert.Equal("Audible", json.RootElement.GetProperty("source").GetString());
                Assert.Equal("B0TESTASIN", json.RootElement.GetProperty("asin").GetString());
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var updated = await db.Audiobooks
                    .Include(a => a.ExternalIdentifiers)
                    .FirstAsync(a => a.Id == audiobookId);

                Assert.Equal("Fixed Metadata Title", updated.Title);
                Assert.Equal("Recovered Subtitle", updated.Subtitle);
                Assert.Contains("Correct Author", updated.Authors ?? new List<string>());
                Assert.Contains("Correct Narrator", updated.Narrators ?? new List<string>());
                Assert.Equal("Test Publisher", updated.Publisher);
                Assert.Equal("<p>Recovered description</p>", updated.Description);
                Assert.True(!string.IsNullOrWhiteSpace(updated.PublishedDate));
                Assert.StartsWith("2024-01-", updated.PublishedDate);
                Assert.Equal("2024", updated.PublishYear);
                Assert.Equal(615, updated.Runtime);
                Assert.Contains("Fantasy", updated.Genres ?? new List<string>());
                Assert.Contains("Epic Fantasy", updated.Genres ?? new List<string>());
                Assert.Contains("9781234567897", updated.Isbn ?? new List<string>());
                Assert.Contains(updated.ExternalIdentifiers ?? new List<AudiobookExternalIdentifier>(), i =>
                    i.Type == AudiobookExternalIdentifierType.Isbn &&
                    i.ValueNormalized == "9781234567897");
            }

            var detailResponse = await client.GetAsync($"/api/v1/library/{audiobookId}");
            detailResponse.EnsureSuccessStatusCode();
            using (var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync()))
            {
                var genres = detailJson.RootElement.GetProperty("genres").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Contains("Fantasy", genres);
                Assert.Contains("Epic Fantasy", genres);
            }

            metadataMock.Verify(m => m.GetMetadataAsync("B0TESTASIN", "us", false), Times.Once);
            asinLookupMock.Verify(
                a => a.GetAsinFromIsbnAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RescanMetadata_Returns429_WhenRepeatedImmediately_ForSameAudiobookAndActor()
        {
            var metadataMock = new Mock<IAudiobookMetadataService>();
            metadataMock
                .Setup(m => m.GetMetadataAsync("B0COOLDWN1", "us", false))
                .ReturnsAsync(new
                {
                    metadata = new AudibleBookResponse
                    {
                        Asin = "B0COOLDWN1",
                        Title = "Cooldown Test"
                    },
                    source = "Audible"
                });

            var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAudiobookMetadataService>();
                    services.AddSingleton(metadataMock.Object);
                });
            });

            int audiobookId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var audiobook = new Audiobook
                {
                    Title = "Cooldown Book",
                    Monitored = true,
                    Asin = "B0COOLDWN1",
                    ExternalIdentifiers = new List<AudiobookExternalIdentifier>
                    {
                        new()
                        {
                            Type = AudiobookExternalIdentifierType.Asin,
                            ValueRaw = "B0COOLDWN1",
                            ValueNormalized = "B0COOLDWN1",
                            Region = "us",
                            IsPrimary = true,
                            Source = AudiobookExternalIdentifierSource.Manual
                        }
                    }
                };

                db.Audiobooks.Add(audiobook);
                await db.SaveChangesAsync();
                audiobookId = audiobook.Id;
            }

            var client = factory.CreateClient();
            var first = await PostRescanAsync(client, audiobookId);
            Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

            var second = await PostRescanAsync(client, audiobookId);
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            Assert.True(second.Headers.TryGetValues("Retry-After", out var retryAfterValues));
            Assert.True(int.TryParse(retryAfterValues!.FirstOrDefault(), out var retryAfter));
            Assert.True(retryAfter > 0);

            using (var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync()))
            {
                Assert.Equal("Rescan cooldown active. Please wait " + retryAfter + " seconds before rescanning this audiobook again.", json.RootElement.GetProperty("message").GetString());
                Assert.Equal(retryAfter, json.RootElement.GetProperty("retryAfterSeconds").GetInt32());
            }

            metadataMock.Verify(m => m.GetMetadataAsync("B0COOLDWN1", "us", false), Times.Once);
        }

        [Fact]
        public async Task RescanMetadata_CapsProviderAttempts_PerRequest()
        {
            var metadataMock = new Mock<IAudiobookMetadataService>();
            metadataMock
                .Setup(m => m.GetMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), false))
                .ReturnsAsync((object?)null);

            var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAudiobookMetadataService>();
                    services.AddSingleton(metadataMock.Object);
                });
            });

            int audiobookId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var audiobook = new Audiobook
                {
                    Title = "Cap Test",
                    Monitored = true,
                    ExternalIdentifiers = Enumerable.Range(1, 10)
                        .Select(i => new AudiobookExternalIdentifier
                        {
                            Type = AudiobookExternalIdentifierType.Asin,
                            ValueRaw = $"B{i:000000000}",
                            ValueNormalized = $"B{i:000000000}",
                            Region = "us",
                            IsPrimary = i == 1,
                            Source = AudiobookExternalIdentifierSource.Manual
                        })
                        .ToList()
                };

                db.Audiobooks.Add(audiobook);
                await db.SaveChangesAsync();
                audiobookId = audiobook.Id;
            }

            var client = factory.CreateClient();
            var response = await PostRescanAsync(client, audiobookId);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            {
                Assert.Equal("No metadata found using the available identifiers.", json.RootElement.GetProperty("message").GetString());
                Assert.False(json.RootElement.TryGetProperty("attempts", out _));
            }

            metadataMock.Verify(
                m => m.GetMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), false),
                Times.Exactly(8));
        }

        private static async Task<HttpResponseMessage> PostRescanAsync(HttpClient client, int audiobookId)
        {
            var csrfToken = await GetAntiforgeryTokenAsync(client);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/library/{audiobookId}/rescan-metadata");
            request.Headers.Add("X-XSRF-TOKEN", csrfToken);
            return await client.SendAsync(request);
        }

        private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
        {
            var tokenResponse = await client.GetAsync("/api/v1/antiforgery/token");
            tokenResponse.EnsureSuccessStatusCode();
            using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
            var csrfToken = tokenJson.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(csrfToken));
            return csrfToken!;
        }
    }
}
