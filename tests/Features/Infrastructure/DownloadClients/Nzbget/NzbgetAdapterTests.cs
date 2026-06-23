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

        private sealed class CancellationAwareReadStream : Stream
        {
            private readonly MemoryStream _innerStream = new(Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\"?><methodResponse><params><param><value><string>25.4</string></value></param></params></methodResponse>"));

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _innerStream.Length;

            public override long Position
            {
                get => _innerStream.Position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _innerStream.Read(buffer, offset, count);
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                return cancellationToken.IsCancellationRequested
                    ? Task.FromCanceled<int>(cancellationToken)
                    : _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                return cancellationToken.IsCancellationRequested
                    ? ValueTask.FromCanceled<int>(cancellationToken)
                    : _innerStream.ReadAsync(buffer, cancellationToken);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _innerStream.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        [Fact]
        public async Task CallAsync_CancellationDuringSend_PropagatesOperationCanceledException()
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(XmlRpcValueResponse("<string>25.4</string>"))
            };
            var handler = new DelegatingHandlerMock((_, observedToken) =>
            {
                cancellationTokenSource.Cancel();
                return observedToken.IsCancellationRequested
                    ? Task.FromCanceled<HttpResponseMessage>(observedToken)
                    : Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var xmlRpcClient = new NzbgetXmlRpcClient(new TestHttpClientFactory(http), "nzbget");
            var request = new NzbgetXmlRpcRequest
            {
                Client = CreateClient(),
                MethodName = "version"
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => xmlRpcClient.CallAsync(request, cancellationToken));
        }

        [Fact]
        public async Task CallAsync_CancellationDuringResponseRead_PropagatesOperationCanceledException()
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancellationAwareReadStream())
            };
            var handler = new DelegatingHandlerMock((_, _) =>
            {
                cancellationTokenSource.Cancel();
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var xmlRpcClient = new NzbgetXmlRpcClient(new TestHttpClientFactory(http), "nzbget");
            var request = new NzbgetXmlRpcRequest
            {
                Client = CreateClient(),
                MethodName = "version"
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => xmlRpcClient.CallAsync(request, cancellationToken));
        }

        [Fact]
        public async Task HistoryReader_VisibleHistory_ParsesAllEntriesInServerOrderAndUsesExactFalseParameter()
        {
            using var apiMock = new NzbgetApiMock();
            var entries = Enumerable.Range(0, 101)
                .Select(index => HistoryEntryValue(
                    nzbId: (index + 1).ToString(),
                    title: $"Ignored Book {index + 1}",
                    status: "WARNING/REPAIRABLE"))
                .Append(HistoryEntryValue(
                    nzbId: "  777  ",
                    title: "Qualifying Book",
                    status: "  success/unpack  ",
                    category: "audiobooks",
                    finalDir: "/final/book",
                    destDir: "/destination/book",
                    fileSizeMb: "12.5",
                    downloadedSizeMb: "99",
                    historyTime: "1700000000"))
                .ToArray();
            apiMock.QueueXmlRpcResponse(
                "history",
                NzbgetApiMock.CreateHistoryResponse(string.Concat(entries)));
            using var http = new HttpClient(apiMock);
            var reader = CreateHistoryReader(http);

            var result = await reader.ReadAsync(CreateClient(), CancellationToken.None);

            Assert.Equal(102, result.Count);
            Assert.Equal("1", result[0].CanonicalNzbId);
            Assert.Equal("777", result[101].CanonicalNzbId);
            Assert.Equal("Qualifying Book", result[101].Title);
            Assert.Equal("audiobooks", result[101].Category);
            Assert.Equal("success/unpack", result[101].RawStatus);
            Assert.Equal(NzbgetHistoryOutcome.Completed, result[101].Outcome);
            Assert.Equal("/final/book", result[101].CompletedPath);
            Assert.Equal(13_107_200, result[101].TotalSizeBytes);
            Assert.Equal(13_107_200, result[101].DownloadedSizeBytes);
            Assert.Equal(
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcDateTime,
                result[101].HistoryTimeUtc);

            var call = Assert.Single(apiMock.XmlRpcCalls);
            Assert.Equal("history", call.MethodName);
            var parameter = Assert.Single(call.Parameters);
            Assert.Equal("0", parameter.Element("boolean")?.Value);
        }

        [Theory]
        [InlineData("SUCCESS/UNPACK", 1)]
        [InlineData("  success/par-check  ", 1)]
        [InlineData("FAILURE/UNPACK", 2)]
        [InlineData("  failure/health  ", 2)]
        [InlineData("WARNING/REPAIRABLE", 0)]
        [InlineData("DELETED/MANUAL", 0)]
        [InlineData("", 0)]
        [InlineData("SUCCESS", 0)]
        [InlineData("UNKNOWN/FUTURE", 0)]
        public async Task HistoryReader_StatusFamilies_ClassifyLiteralContract(
            string status,
            int expectedOutcome)
        {
            using var apiMock = new NzbgetApiMock();
            apiMock.QueueXmlRpcResponse(
                "history",
                NzbgetApiMock.CreateHistoryResponse(
                    HistoryEntryValue(nzbId: "42", title: "Book", status: status)));
            using var http = new HttpClient(apiMock);
            var reader = CreateHistoryReader(http);

            var entry = Assert.Single(await reader.ReadAsync(CreateClient(), CancellationToken.None));

            Assert.Equal(status.Trim(), entry.RawStatus);
            Assert.Equal((NzbgetHistoryOutcome)expectedOutcome, entry.Outcome);
        }

        [Fact]
        public async Task HistoryReader_Fields_ApplyPathNumericTimeAndFallbackSemantics()
        {
            using var apiMock = new NzbgetApiMock();
            apiMock.QueueXmlRpcResponse(
                "history",
                NzbgetApiMock.CreateHistoryResponse(string.Concat(
                    HistoryEntryValue(
                        nzbId: "not-a-number",
                        legacyId: "999",
                        title: "Title Fallback Available",
                        status: "SUCCESS/ALL",
                        finalDir: "   ",
                        destDir: "/destination/fallback",
                        fileSizeMb: "-5",
                        downloadedSizeMb: "-1",
                        historyTime: "invalid"),
                    HistoryEntryValue(
                        nzbId: null,
                        title: null,
                        status: "FAILURE/PAR",
                        finalDir: null,
                        destDir: null,
                        fileSizeMb: "999999999999999999999",
                        downloadedSizeMb: "999999999999999999999",
                        historyTime: "999999999999999999999"))));
            using var http = new HttpClient(apiMock);
            var reader = CreateHistoryReader(http);

            var result = await reader.ReadAsync(CreateClient(), CancellationToken.None);

            Assert.Collection(
                result,
                first =>
                {
                    Assert.Equal(string.Empty, first.CanonicalNzbId);
                    Assert.Equal("Title Fallback Available", first.Title);
                    Assert.Equal("/destination/fallback", first.CompletedPath);
                    Assert.Equal(0, first.TotalSizeBytes);
                    Assert.Equal(0, first.DownloadedSizeBytes);
                    Assert.Null(first.HistoryTimeUtc);
                },
                second =>
                {
                    Assert.Equal(string.Empty, second.CanonicalNzbId);
                    Assert.Equal(string.Empty, second.Title);
                    Assert.Equal(string.Empty, second.Category);
                    Assert.Equal(string.Empty, second.DestDir);
                    Assert.Equal(string.Empty, second.FinalDir);
                    Assert.Equal(string.Empty, second.CompletedPath);
                    Assert.Equal(long.MaxValue, second.TotalSizeBytes);
                    Assert.Equal(long.MaxValue, second.DownloadedSizeBytes);
                    Assert.Null(second.HistoryTimeUtc);
                });
        }

        [Fact]
        public async Task HistoryReader_MalformedWholeResponseShape_Throws()
        {
            using var apiMock = new NzbgetApiMock();
            apiMock.QueueXmlRpcResponse("history", XmlRpcValueResponse("<string>not-an-array</string>"));
            using var http = new HttpClient(apiMock);
            var reader = CreateHistoryReader(http);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => reader.ReadAsync(CreateClient(), CancellationToken.None));

            Assert.Contains("array/data", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task HistoryReader_Cancellation_PropagatesSameToken()
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            var handler = new DelegatingHandlerMock((_, observedToken) =>
            {
                Assert.True(observedToken.CanBeCanceled);
                cancellationTokenSource.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(observedToken);
            });
            using var http = new HttpClient(handler);
            var reader = CreateHistoryReader(http);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => reader.ReadAsync(CreateClient(), cancellationToken));

            Assert.True(cancellationToken.IsCancellationRequested);
        }

        [Fact]
        public void HistoryReader_ParseBoundary_CancellationPropagates()
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();
            var result = XElement.Parse(
                "<value><array><data><value><struct /></value></data></array></value>");

            var exception = Assert.Throws<OperationCanceledException>(
                () => NzbgetHistoryReader.ParseEntries(result, cancellationTokenSource.Token));

            Assert.Equal(cancellationTokenSource.Token, exception.CancellationToken);
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

        private static NzbgetHistoryReader CreateHistoryReader(HttpClient http)
        {
            return new NzbgetHistoryReader(
                new NzbgetXmlRpcClient(new TestHttpClientFactory(http), "nzbget"));
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

        private static string HistoryEntryValue(
            string? nzbId,
            string? title,
            string status,
            string? category = null,
            string? finalDir = null,
            string? destDir = null,
            string? fileSizeMb = null,
            string? downloadedSizeMb = null,
            string? historyTime = null,
            string? legacyId = null)
        {
            var members = new[]
            {
                HistoryMember("NZBID", nzbId),
                HistoryMember("ID", legacyId),
                HistoryMember("NZBName", title),
                HistoryMember("Category", category),
                HistoryMember("Status", status),
                HistoryMember("FinalDir", finalDir),
                HistoryMember("DestDir", destDir),
                HistoryMember("FileSizeMB", fileSizeMb),
                HistoryMember("DownloadedSizeMB", downloadedSizeMb),
                HistoryMember("HistoryTime", historyTime)
            };

            return $"<value><struct>{string.Concat(members)}</struct></value>";
        }

        private static string HistoryMember(string name, string? value)
        {
            return value == null
                ? string.Empty
                : new XElement(
                    "member",
                    new XElement("name", name),
                    new XElement("value", new XElement("string", value)))
                    .ToString(SaveOptions.DisableFormatting);
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
