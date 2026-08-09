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
using System.Text;
using Listenarr.Domain.Integrations;
using Listenarr.Infrastructure.Integrations.Audiobookshelf;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Integrations.Audiobookshelf
{
    [Trait("Name", "AudiobookshelfServiceTests")]
    [Trait("Category", "Audiobookshelf")]
    public class AudiobookshelfServiceTests : BaseTests
    {
        private const string LibrariesPayload = """
            {
              "libraries": [
                { "id": "lib_books", "name": "Audiobooks", "mediaType": "book" },
                { "id": "lib_books2", "name": "More Audiobooks", "mediaType": "book" },
                { "id": "lib_pods", "name": "Podcasts", "mediaType": "podcast" }
              ]
            }
            """;

        private AudiobookshelfService CreateService(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            var httpClient = new HttpClient(new DelegatingHandlerMock(handler));
            return new AudiobookshelfService(
                httpClient,
                _provider.GetRequiredService<IConfigurationService>(),
                _provider.GetRequiredService<ILogger<AudiobookshelfService>>());
        }

        private async Task SaveConnectionAsync(string? libraryId = null)
        {
            var svc = _provider.GetRequiredService<IConfigurationService>();
            await svc.SaveAudiobookshelfSettingsAsync(new AudiobookshelfConnectionSettings
            {
                Url = "http://localhost:13378",
                ApiKey = "abs-token",
                LibraryId = libraryId,
            });
        }

        private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        [Fact]
        public async Task TestConnection_ParsesLibraries_AndSendsBearerToken()
        {
            await SaveConnectionAsync();
            var requests = new List<HttpRequestMessage>();
            var service = CreateService((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(Json(LibrariesPayload));
            });

            var result = await service.TestConnectionAsync(null, null);

            Assert.True(result.Success);
            Assert.NotNull(result.Libraries);
            Assert.Equal(3, result.Libraries!.Count);
            Assert.Equal("Audiobooks", result.Libraries[0].Name);

            var request = Assert.Single(requests);
            Assert.Equal("/api/libraries", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("abs-token", request.Headers.Authorization?.Parameter);
        }

        [Fact]
        public async Task TestConnection_Unauthorized_ReportsRejectedToken()
        {
            await SaveConnectionAsync();
            var service = CreateService((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

            var result = await service.TestConnectionAsync(null, null);

            Assert.False(result.Success);
            Assert.Contains("rejected", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnection_WithoutConfiguration_Fails()
        {
            var service = CreateService((_, _) => Task.FromResult(Json(LibrariesPayload)));

            var result = await service.TestConnectionAsync(null, null);

            Assert.False(result.Success);
        }

        [Fact]
        public async Task TriggerScan_ScansOnlyConfiguredLibrary()
        {
            await SaveConnectionAsync(libraryId: "lib_books2");
            var scanRequests = new List<string>();
            var service = CreateService((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    scanRequests.Add(request.RequestUri!.AbsolutePath);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }

                return Task.FromResult(Json(LibrariesPayload));
            });

            var result = await service.TriggerScanAsync();

            Assert.True(result.Success);
            Assert.Equal(["/api/libraries/lib_books2/scan"], scanRequests);
            Assert.Contains("More Audiobooks", result.Message);
        }

        [Fact]
        public async Task TriggerScan_WithoutConfiguredLibrary_ScansAllBookLibraries()
        {
            await SaveConnectionAsync();
            var scanRequests = new List<string>();
            var service = CreateService((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    scanRequests.Add(request.RequestUri!.AbsolutePath);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }

                return Task.FromResult(Json(LibrariesPayload));
            });

            var result = await service.TriggerScanAsync();

            Assert.True(result.Success);
            Assert.Equal(["/api/libraries/lib_books/scan", "/api/libraries/lib_books2/scan"], scanRequests);
            // Podcast libraries are not scanned
            Assert.DoesNotContain("/api/libraries/lib_pods/scan", scanRequests);
        }

        [Fact]
        public async Task TriggerScan_AllScanRequestsFail_ReportsFailure()
        {
            await SaveConnectionAsync(libraryId: "lib_books");
            var service = CreateService((request, _) =>
                Task.FromResult(request.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Json(LibrariesPayload)));

            var result = await service.TriggerScanAsync();

            Assert.False(result.Success);
        }
    }
}
