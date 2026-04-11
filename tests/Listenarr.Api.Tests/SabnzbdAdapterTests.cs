using System.Net;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models.Configurations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
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

            using var http = new HttpClient(handler);
            var adapter = new SabnzbdAdapter(
                new TestHttpClientFactory(http),
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
