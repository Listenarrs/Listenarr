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
using Listenarr.Infrastructure.Adapters;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using System.Xml.Linq;

namespace Listenarr.Tests.Features.Infrastructure.Adapters
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

        [Theory]
        [InlineData(false, "GroupDelete")]
        [InlineData(true, "GroupDeleteFinal")]
        public async Task RemoveAsync_FallsBackToQueueWithConfiguredFilePolicy(
            bool deleteFiles,
            string expectedCommand)
        {
            var requests = new List<string>();
            var handler = new DelegatingHandlerMock(async (request, ct) =>
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                requests.Add(body);
                var command = XDocument.Parse(body)
                    .Descendants("param")
                    .First()
                    .Value;
                var succeeded = command == expectedCommand;
                return MockUtils.GetCannedResponse(
                    $"<?xml version=\"1.0\"?><methodResponse><params><param><value><boolean>{(succeeded ? "1" : "0")}</boolean></value></param></params></methodResponse>",
                    "text/xml");
            });

            using var http = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<NzbgetAdapter>.Instance);
            var client = new DownloadClientConfiguration
            {
                Host = "localhost",
                Port = 6789
            };

            var result = await adapter.RemoveAsync(client, "123", deleteFiles);

            Assert.True(result);
            Assert.Equal(2, requests.Count);
            Assert.Contains("HistoryDelete", requests[0], StringComparison.Ordinal);
            Assert.Contains(expectedCommand, requests[1], StringComparison.Ordinal);
            Assert.All(requests, body => Assert.Contains("<i4>123</i4>", body, StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("SUCCESS", DownloadItemStatus.Completed, "completed")]
        [InlineData("SUCCESS/UNPACK", DownloadItemStatus.Completed, "completed")]
        [InlineData("FAILURE", DownloadItemStatus.Failed, "failed")]
        public void GroupStatus_IsMappedWithoutBehaviorDrift(
            string status,
            DownloadItemStatus expectedItemStatus,
            string expectedQueueStatus)
        {
            var client = new DownloadClientConfiguration
            {
                Id = "nzbget-1",
                Name = "NZBGet",
                Type = "nzbget"
            };
            var structElement = XElement.Parse(
                $$"""
                <struct>
                  <member><name>GroupID</name><value><i4>123</i4></value></member>
                  <member><name>NZBName</name><value><string>Book</string></value></member>
                  <member><name>Status</name><value><string>{{status}}</string></value></member>
                  <member><name>FileSizeMB</name><value><string>100</string></value></member>
                  <member><name>RemainingSizeMB</name><value><string>0</string></value></member>
                  <member><name>DestDir</name><value><string>/downloads</string></value></member>
                </struct>
                """);

            var clientItem = NzbgetResponseMapper.MapGroupToDownloadClientItem(client, structElement);
            var queueItem = NzbgetResponseMapper.MapGroup(client, structElement);

            Assert.Equal(expectedItemStatus, clientItem.Status);
            Assert.Equal(expectedQueueStatus, queueItem.Status);
            Assert.Equal(0, clientItem.RemainingSize);
        }
    }
}
