using System.Net;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models.Configurations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class NzbgetAdapterTests
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
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><methodResponse><params><param><value><string>25.4</string></value></param></params></methodResponse>")
            };
            var handler = new DelegatingHandlerMock((req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(response);
            });

            using var http = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                Mock.Of<IRemotePathMappingService>(),
                NullLogger<NzbgetAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111/nzbget",
                Port = 6789,
                UseSSL = false,
                Username = "Talis",
                Password = "secret"
            };

            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.Contains("connected", message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(capturedUri);
            Assert.Equal("http", capturedUri!.Scheme);
            Assert.Equal("192.168.50.111", capturedUri.Host);
            Assert.Equal(6789, capturedUri.Port);
            Assert.Equal("/xmlrpc", capturedUri.AbsolutePath);
        }

        [Fact]
        public async Task TestConnectionAsync_PrefersExplicitPortAndSslOverEmbeddedHostUri()
        {
            Uri? capturedUri = null;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><methodResponse><params><param><value><string>25.4</string></value></param></params></methodResponse>")
            };
            var handler = new DelegatingHandlerMock((req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(response);
            });

            using var http = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                Mock.Of<IRemotePathMappingService>(),
                NullLogger<NzbgetAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111:9999/legacy",
                Port = 6789,
                UseSSL = true
            };

            var (success, _) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.NotNull(capturedUri);
            Assert.Equal("https", capturedUri!.Scheme);
            Assert.Equal("192.168.50.111", capturedUri.Host);
            Assert.Equal(6789, capturedUri.Port);
            Assert.Equal("/xmlrpc", capturedUri.AbsolutePath);
        }

        [Fact]
        public async Task GetQueueAsync_NormalizesHostWithSchemeAndPath()
        {
            Uri? capturedUri = null;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><methodResponse><params><param><value><array><data></data></array></value></param></params></methodResponse>")
            };
            var handler = new DelegatingHandlerMock((req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(response);
            });

            using var http = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                Mock.Of<IRemotePathMappingService>(),
                NullLogger<NzbgetAdapter>.Instance);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111/nzbget",
                Port = 6789,
                UseSSL = false,
                Username = "Talis",
                Password = "secret"
            };

            var queue = await adapter.GetQueueAsync(client);

            Assert.NotNull(queue);
            Assert.Empty(queue);
            Assert.NotNull(capturedUri);
            Assert.Equal("http", capturedUri!.Scheme);
            Assert.Equal("192.168.50.111", capturedUri.Host);
            Assert.Equal(6789, capturedUri.Port);
            Assert.Equal("/xmlrpc", capturedUri.AbsolutePath);
        }
    }
}
