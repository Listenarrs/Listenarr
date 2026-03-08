using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class TransmissionAdapterTests
    {
        private sealed class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;

            public TestHttpClientFactory(HttpClient client)
            {
                _client = client;
            }

            public HttpClient CreateClient(string name)
            {
                return _client;
            }
        }

        [Fact]
        [Trait("Area", "TransmissionAdd")]
        [Trait("Scenario", "PreservesEncodedTrackerSeparatorsWhenNormalizingMagnetUri")]
        public async Task AddAsync_MagnetWithEncodedTrackerSeparators_DoesNotCorruptTrackerQuery()
        {
            string? postedFilename = null;

            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"result":"success","arguments":{"torrent-added":{"id":1,"hashString":"HASH1","name":"Book"}}}""")
            };
            var handler = new DelegatingHandlerMock(async (req, ct) =>
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                postedFilename = doc.RootElement.GetProperty("arguments").GetProperty("filename").GetString();
                return response;
            });

            using var httpClient = new HttpClient(handler);
            var adapter = new TransmissionAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<TransmissionAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Id = "tr-1",
                Name = "Transmission",
                Type = "transmission",
                Host = "localhost",
                Port = 9091
            };

            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890&tr=http%3A%2F%2Ftracker.example.com%2Fannounce%3Ffoo%3D1%26bar%3D2&dn=Book%20Title"
            };

            var addedId = await adapter.AddAsync(client, searchResult);

            Assert.Equal("HASH1", addedId);
            Assert.Equal(
                "magnet:?xt=urn:btih:ABCDEF1234567890&tr=http%3A%2F%2Ftracker.example.com%2Fannounce%3Ffoo%3D1%26bar%3D2&dn=Book Title",
                postedFilename);
        }
    }
}
