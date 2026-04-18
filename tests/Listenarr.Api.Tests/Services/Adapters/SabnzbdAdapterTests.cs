using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Listenarr.Api.Tests
{
    [Trait("Category", "DownloadClientAdapter")]
    [Trait("Third-Party", "SABnzbd")]
    public class SabnzbdAdapterTests
    {
        private sealed class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;

            public TestHttpClientFactory(HttpClient client)
            {
                _client = client;
            }

            public HttpClient CreateClient(string name) => _client;
        }

        [Fact]
        public async Task TestConnectionAsync_NormalizesHostWithSchemeAndPath()
        {
            Uri? capturedUri = null;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"version":"4.4.1"}""")
            };
            var handler = new DelegatingHandlerMock((req, ct) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(response);
            });

            var adapter = new SabnzbdAdapter(
                new Mock<IDbContextFactory<ListenArrDbContext>>().Object,
                new HttpClient(handler),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<SabnzbdAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111/sab",
                Port = 8080,
                UseSSL = false,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "secret"
                }
            };

            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.Contains("connected", message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(capturedUri);
            Assert.Equal("http", capturedUri!.Scheme);
            Assert.Equal("192.168.50.111", capturedUri.Host);
            Assert.Equal(8080, capturedUri.Port);
            Assert.Equal("/api", capturedUri.AbsolutePath);
            Assert.Contains("mode=version", capturedUri.Query, StringComparison.Ordinal);
            Assert.Contains("output=json", capturedUri.Query, StringComparison.Ordinal);
        }
    }
}
