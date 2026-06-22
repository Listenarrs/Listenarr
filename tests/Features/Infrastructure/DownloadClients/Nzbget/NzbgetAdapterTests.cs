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

using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;
using Microsoft.Extensions.Logging.Abstractions;
using System.Xml.Linq;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Nzbget
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
        public async Task TestConnectionAsync_VersionXmlRpcCompatibility_PreservesMethodParametersAndResponse()
        {
            using var apiMock = new NzbgetApiMock();
            apiMock.QueueXmlRpcResponse("version", XmlRpcValueResponse("<string>25.4</string>"));
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);

            var result = await adapter.TestConnectionAsync(CreateClient());

            Assert.True(result.Success);
            Assert.Equal("NZBGet: connected", result.Message);
            var call = Assert.Single(apiMock.XmlRpcCalls);
            Assert.Equal("version", call.MethodName);
            Assert.Empty(call.Parameters);
        }

        [Fact]
        public async Task AddAsync_AppendXmlRpcCompatibility_PreservesMethodParametersAndResponse()
        {
            using var apiMock = new NzbgetApiMock();
            apiMock.QueueXmlRpcResponse("append", XmlRpcValueResponse("<i4>321</i4>"));
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var client = CreateClient();
            client.Settings = new Dictionary<string, object>
            {
                ["category"] = "audiobooks",
                ["recentPriority"] = "high"
            };
            var submission = new PreparedUsenetSubmission(
                "Compatibility Book",
                "Author",
                "Album",
                "Indexer",
                "Lossless",
                "English",
                3,
                "https://indexer.test/book.nzb",
                [1, 2, 3],
                "compatibility-book.nzb");

            var result = await adapter.AddAsync(client, submission);

            Assert.Equal("321", result.ExternalId);
            Assert.False(result.WasDuplicate);
            var call = Assert.Single(apiMock.XmlRpcCalls);
            Assert.Equal("append", call.MethodName);
            Assert.Equal(10, call.Parameters.Count);
            Assert.Equal("compatibility-book.nzb", call.Parameters[0].Element("string")?.Value);
            Assert.Equal("AQID", call.Parameters[1].Element("string")?.Value);
            Assert.Equal("audiobooks", call.Parameters[2].Element("string")?.Value);
            Assert.Equal("50", call.Parameters[3].Element("i4")?.Value);
            Assert.Equal("0", call.Parameters[4].Element("boolean")?.Value);
            Assert.Equal("0", call.Parameters[5].Element("boolean")?.Value);
            Assert.Equal(string.Empty, call.Parameters[6].Element("string")?.Value);
            Assert.Equal("0", call.Parameters[7].Element("i4")?.Value);
            Assert.Equal("SCORE", call.Parameters[8].Element("string")?.Value);

            var postProcessingParameter = Assert.Single(
                call.Parameters[9].Element("array")!.Element("data")!.Elements("value"));
            var members = ReadStructMembers(postProcessingParameter.Element("struct")!);
            Assert.Equal("drone", members["Name"]);
            Assert.Matches("^[0-9a-f]{32}$", members["Value"]);
            Assert.Equal(members["Value"], result.ContentId);
        }

        [Fact]
        public async Task RemoveAsync_HistoryDeleteXmlRpcCompatibility_PreservesMethodParametersAndResponse()
        {
            using var apiMock = new NzbgetApiMock();
            apiMock.QueueXmlRpcResponse("editqueue", XmlRpcValueResponse("<boolean>1</boolean>"));
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);

            var result = await adapter.RemoveAsync(CreateClient(), "123", deleteFiles: true);

            Assert.True(result);
            var call = Assert.Single(apiMock.XmlRpcCalls);
            AssertEditQueueCall(call, "HistoryDelete", 123);
        }

        [Theory]
        [InlineData(false, "GroupDelete")]
        [InlineData(true, "GroupDeleteFinal")]
        public async Task RemoveAsync_GroupDeleteXmlRpcCompatibility_PreservesMethodParametersAndResponse(
            bool deleteFiles,
            string expectedCommand)
        {
            using var apiMock = new NzbgetApiMock();
            apiMock.QueueXmlRpcResponse("editqueue", XmlRpcValueResponse("<boolean>0</boolean>"));
            apiMock.QueueXmlRpcResponse("editqueue", XmlRpcValueResponse("<boolean>1</boolean>"));
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);

            var result = await adapter.RemoveAsync(CreateClient(), "123", deleteFiles);

            Assert.True(result);
            Assert.Collection(
                apiMock.XmlRpcCalls,
                call => AssertEditQueueCall(call, "HistoryDelete", 123),
                call => AssertEditQueueCall(call, expectedCommand, 123));
        }

        [Fact]
        public async Task GetRecentHistoryAsync_HistoryFalseXmlRpcCompatibility_PreservesMethodParametersAndResponse()
        {
            using var apiMock = new NzbgetApiMock();
            apiMock.QueueXmlRpcResponse(
                "history",
                XmlRpcValueResponse(
                    """
                    <array><data>
                      <value><struct>
                        <member><name>ID</name><value><i4>101</i4></value></member>
                        <member><name>NZBName</name><value><string>First Book</string></value></member>
                      </struct></value>
                      <value><struct>
                        <member><name>ID</name><value><i4>202</i4></value></member>
                        <member><name>NZBName</name><value><string>Second Book</string></value></member>
                      </struct></value>
                      <value><struct>
                        <member><name>ID</name><value><i4>303</i4></value></member>
                        <member><name>NZBName</name><value><string>Beyond Limit</string></value></member>
                      </struct></value>
                    </data></array>
                    """));
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);

            var result = await adapter.GetRecentHistoryAsync(CreateClient(), limit: 2);

            Assert.Equal([("101", "First Book"), ("202", "Second Book")], result);
            var call = Assert.Single(apiMock.XmlRpcCalls);
            Assert.Equal("history", call.MethodName);
            var parameter = Assert.Single(call.Parameters);
            Assert.Equal("0", parameter.Element("boolean")?.Value);
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
                  <member><name>NZBID</name><value><i4>999</i4></value></member>
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

            var lastIdOnly = XElement.Parse(
                "<struct><member><name>NZBID</name><value><i4>999</i4></value></member><member><name>LastID</name><value><i4>456</i4></value></member></struct>");
            var generatedId = NzbgetResponseMapper.MapGroupToDownloadClientItem(
                client,
                XElement.Parse("<struct><member><name>NZBID</name><value><i4>999</i4></value></member></struct>"))
                .DownloadId;
            Assert.Equal("123", clientItem.DownloadId);
            Assert.Equal("456", NzbgetResponseMapper.MapGroup(client, lastIdOnly).Id);
            Assert.Matches("^[0-9A-F]{32}$", generatedId);
        }

        private static NzbgetAdapter CreateAdapter(HttpClient http)
        {
            return new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<NzbgetAdapter>.Instance);
        }

        private static DownloadClientConfiguration CreateClient()
        {
            return new DownloadClientConfiguration
            {
                Host = "localhost",
                Port = 6789
            };
        }

        private static string XmlRpcValueResponse(string serializedValue)
        {
            return $"<?xml version=\"1.0\"?><methodResponse><params><param><value>{serializedValue}</value></param></params></methodResponse>";
        }

        private static IReadOnlyDictionary<string, string> ReadStructMembers(XElement structElement)
        {
            return structElement.Elements("member").ToDictionary(
                member => member.Element("name")!.Value,
                member => member.Element("value")!.Elements().Single().Value,
                StringComparer.Ordinal);
        }

        private static void AssertEditQueueCall(
            NzbgetApiMock.XmlRpcCall call,
            string expectedCommand,
            int expectedId)
        {
            Assert.Equal("editqueue", call.MethodName);
            Assert.Equal(4, call.Parameters.Count);
            Assert.Equal(expectedCommand, call.Parameters[0].Element("string")?.Value);
            Assert.Equal("0", call.Parameters[1].Element("i4")?.Value);
            Assert.Equal(string.Empty, call.Parameters[2].Element("string")?.Value);
            var id = Assert.Single(
                call.Parameters[3].Element("array")!.Element("data")!.Elements("value"));
            Assert.Equal(expectedId.ToString(), id.Element("i4")?.Value);
        }
    }
}
