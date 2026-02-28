using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class QbittorrentAdapterTests
    {
        private class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public TestHttpClientFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }

        [Fact]
        public async Task TestConnection_When_VersionForbidden_Then_LoginSucceeds_ReturnsSuccess()
        {
            var loggedIn = false;

            var handler = new DelegatingHandlerMock((req, ct) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.StartsWith("/api/v2/app/version", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(!loggedIn ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
                    {
                        Content = new StringContent(!loggedIn ? "Forbidden" : "v5.0.2")
                    });
                }

                if (req.Method == HttpMethod.Post && req.RequestUri.PathAndQuery.StartsWith("/api/v2/auth/login", StringComparison.OrdinalIgnoreCase))
                {
                    loggedIn = true;
                    var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok") };
                    resp.Headers.Add("Set-Cookie", "SID=1; HttpOnly; Path=/");
                    return Task.FromResult(resp);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            var http = new HttpClient(handler);
            var factory = new TestHttpClientFactory(http);
            var pathMapMock = new Mock<Listenarr.Api.Services.IRemotePathMappingService>();
            var adapter = new QbittorrentAdapter(factory, pathMapMock.Object, NullLogger<QbittorrentAdapter>.Instance);

            var cfg = new DownloadClientConfiguration
            {
                Host = "localhost",
                Port = 443,
                UseSSL = true,
                Username = "admin",
                Password = "admin"
            };

            var (success, message) = await adapter.TestConnectionAsync(cfg);

            Assert.True(success);
            Assert.Contains("connected", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnection_When_VersionForbidden_And_NoCredentials_ReturnsForbidden()
        {
            var handler = new DelegatingHandlerMock((req, ct) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.StartsWith("/api/v2/app/version", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Forbidden") });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            var http = new HttpClient(handler);
            var factory = new TestHttpClientFactory(http);
            var pathMapMock = new Mock<Listenarr.Api.Services.IRemotePathMappingService>();
            var adapter = new QbittorrentAdapter(factory, pathMapMock.Object, NullLogger<QbittorrentAdapter>.Instance);

            var cfg = new DownloadClientConfiguration
            {
                Host = "localhost",
                Port = 443,
                UseSSL = true,
                Username = null,
                Password = null
            };

            var (success, message) = await adapter.TestConnectionAsync(cfg);

            Assert.False(success);
            Assert.Contains("Forbidden", message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
