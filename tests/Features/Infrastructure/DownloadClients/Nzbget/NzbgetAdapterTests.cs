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
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Listenarr.Domain.Downloads.Exceptions;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;
using Microsoft.Extensions.Logging.Abstractions;
using System.Xml.Linq;
using Xunit.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Nzbget
{
    public class NzbgetAdapterTests
    {
        private const int PerformanceHistoryEntryCount = 1_000;
        private const int PerformanceBeyondOneHundredIndex = 752;
        private readonly ITestOutputHelper _output;

        private delegate void MergeHistoryDelegate(
            DownloadClientConfiguration client,
            IReadOnlyList<NzbgetHistoryEntry> history,
            IReadOnlyDictionary<string, Download> trackedById,
            IReadOnlyList<Download> trackedDownloads,
            ISet<Download> matchedDownloads,
            ISet<string> activeCanonicalIds,
            CancellationToken cancellationToken);

        private static readonly MergeHistoryDelegate MergeHistoryForPerformanceTest =
            CreateMergeHistoryDelegate();

        private sealed record CapturedLog(
            LogLevel Level,
            string Message,
            IReadOnlyDictionary<string, object?> State);

        public NzbgetAdapterTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<CapturedLog> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IEnumerable<KeyValuePair<string, object?>>;
                Entries.Add(
                    new CapturedLog(
                        logLevel,
                        formatter(state, exception),
                        values?.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal) ??
                        new Dictionary<string, object?>()));
            }
        }

        private sealed class SequenceTimeProvider(params long[] timestamps) : TimeProvider
        {
            private readonly Queue<long> _timestamps = new(timestamps);

            public override long TimestampFrequency => 1_000;

            public override long GetTimestamp()
            {
                return _timestamps.Dequeue();
            }
        }

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
        public void NzbgetHistory_ParseAndMerge_OneThousandVisibleEntries_WithinGuardrails()
        {
            var historyResult = CreatePerformanceHistoryResult();
            var warmState = CreatePerformanceMergeState();
            var warmHistory = NzbgetHistoryReader.ParseEntries(
                historyResult,
                CancellationToken.None);
            MergeHistoryForPerformanceTest(
                warmState.Client,
                warmHistory,
                warmState.TrackedById,
                warmState.Downloads,
                warmState.MatchedDownloads,
                warmState.ActiveCanonicalIds,
                CancellationToken.None);

            var measuredState = CreatePerformanceMergeState();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var startTimestamp = Stopwatch.GetTimestamp();
            var measuredHistory = NzbgetHistoryReader.ParseEntries(
                historyResult,
                CancellationToken.None);
            MergeHistoryForPerformanceTest(
                measuredState.Client,
                measuredHistory,
                measuredState.TrackedById,
                measuredState.Downloads,
                measuredState.MatchedDownloads,
                measuredState.ActiveCanonicalIds,
                CancellationToken.None);
            var endTimestamp = Stopwatch.GetTimestamp();
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var elapsedMilliseconds = Stopwatch
                .GetElapsedTime(startTimestamp, endTimestamp)
                .TotalMilliseconds;

            _output.WriteLine(
                $"elapsedMs={elapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}");
            _output.WriteLine($"allocatedBytes={allocatedBytes}");

            Assert.Equal(PerformanceHistoryEntryCount, measuredHistory.Count);
            Assert.Equal(
                DownloadStatus.Completed,
                measuredState.BeyondOneHundredDownload.Status);
            Assert.Equal(DownloadStatus.Downloading, measuredState.Downloads[0].Status);
            Assert.Equal(DownloadStatus.Queued, measuredState.Downloads[996].Status);
            Assert.True(
                elapsedMilliseconds <= 500,
                $"elapsedMs={elapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}");
            Assert.True(
                allocatedBytes <= 33_554_432,
                $"allocatedBytes={allocatedBytes}");
        }

        [Fact]
        public async Task FetchDownloadsAsync_ActiveAndCompletedHistory_MutatesExistingObjectsWithoutChangingListShape()
        {
            // AC: Active polling remains unchanged and completed history mutates only an unmatched tracked Download.
            // Behavior: Public poll -> active-first plus typed history -> same list/object order with completed FinalDir state.
            // @category: integration
            // @lane: integration
            // @dependency: NZBGet JSON-RPC polling and XML-RPC history reader
            // @complexity: medium
            // Value Score: 30
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [ActiveGroup(101, "Active Book", "DOWNLOADING", "other")],
                [
                    HistoryEntryValue(
                        nzbId: "202",
                        title: "Completed Book",
                        status: "SUCCESS/UNPACK",
                        category: "audiobooks",
                        finalDir: "/final/completed-book",
                        destDir: "/destination/completed-book",
                        fileSizeMb: "50",
                        downloadedSizeMb: "50")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var activeDownload = CreateDownload("active", "Active Book", "101", 100);
            var completedDownload = CreateDownload("completed", "Completed Book", "202", 50);
            var downloads = new List<Download> { activeDownload, completedDownload };

            var result = await adapter.FetchDownloadsAsync(CreateClient(), downloads, CancellationToken.None);

            Assert.Same(downloads, result);
            Assert.Equal(2, result.Count);
            Assert.Same(activeDownload, result[0]);
            Assert.Same(completedDownload, result[1]);
            Assert.Equal(DownloadStatus.Downloading, activeDownload.Status);
            Assert.Equal(0.75m, activeDownload.Progress);
            Assert.Equal(786_432, activeDownload.DownloadedSize);
            Assert.Equal(DownloadStatus.Completed, completedDownload.Status);
            Assert.Equal(100m, completedDownload.Progress);
            Assert.Equal(0L, completedDownload.Metadata["AmountLeft"]);
            Assert.Equal("/final/completed-book", completedDownload.DownloadPath);
            Assert.Equal(
                [
                    new NzbgetApiMock.JsonRpcCall("status", """{"method":"status","id":2}"""),
                    new NzbgetApiMock.JsonRpcCall("listgroups", """{"method":"listgroups","id":3}""")
                ],
                apiMock.JsonRpcCalls);
            var historyCall = Assert.Single(apiMock.XmlRpcCalls);
            Assert.Equal("history", historyCall.MethodName);
            Assert.Equal("0", Assert.Single(historyCall.Parameters).Element("boolean")?.Value);
        }

        [Fact]
        public async Task FetchDownloadsAsync_CompletedHistory_UsesDestDirAndHistorySizeAsExactTerminalState()
        {
            // AC: AC-NZB-003 and AC-NZB-007 require completed state and DestDir fallback when FinalDir is empty.
            // Behavior: Different initial/history sizes with empty FinalDir -> public poll -> exact history size and terminal fields.
            // @category: integration
            // @lane: integration
            // @dependency: typed history size parsing and completed-path resolution
            // @complexity: medium
            // Value Score: 32
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [],
                [
                    HistoryEntryValue(
                        nzbId: "250",
                        title: "DestDir Book",
                        status: "SUCCESS/UNPACK",
                        finalDir: string.Empty,
                        destDir: "/destination/destdir-book",
                        fileSizeMb: "80",
                        downloadedSizeMb: "60")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var download = CreateDownload("destdir", "DestDir Book", "250", 10);
            download.DownloadedSize = 2L * 1024 * 1024;
            download.Progress = 20;

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [download],
                CancellationToken.None);

            Assert.Equal(80L * 1024 * 1024, download.TotalSize);
            Assert.Equal(80L * 1024 * 1024, download.DownloadedSize);
            Assert.Equal(100m, download.Progress);
            Assert.Equal(0L, download.Metadata["AmountLeft"]);
            Assert.Equal(DownloadStatus.Completed, download.Status);
            Assert.Equal("/destination/destdir-book", download.DownloadPath);
        }

        [Fact]
        public async Task FetchDownloadsAsync_FailedHistory_MapsExactFailureFieldsAndDerivedProgress()
        {
            // AC: AC-NZB-004 maps FAILURE/* to failed with exact trimmed failure context.
            // Behavior: Failed history -> public poll mutation -> exact status, progress, remaining, and failure fields.
            // @category: core-functionality
            // @lane: integration
            // @dependency: NZBGet JSON-RPC polling and XML-RPC history reader
            // @complexity: medium
            // Value Score: 28
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [],
                [
                    HistoryEntryValue(
                        nzbId: "301",
                        title: "Failed Book",
                        status: "  FAILURE/UNPACK  ",
                        fileSizeMb: "100",
                        downloadedSizeMb: "25")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var download = CreateDownload("failed", "Failed Book", "301", 100);
            download.DownloadPath = "/existing/path";

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [download],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Failed, download.Status);
            Assert.Equal(25m, download.Progress);
            Assert.Equal(25L * 1024 * 1024, download.DownloadedSize);
            Assert.Equal(75L * 1024 * 1024, download.Metadata["AmountLeft"]);
            Assert.Equal("FAILURE/UNPACK", download.ErrorMessage);
            Assert.Equal("FAILURE/UNPACK", download.Metadata["ClientFailureReason"]);
            Assert.Equal("/existing/path", download.DownloadPath);
        }

        [Fact]
        public async Task FetchDownloadsAsync_HistoryMatching_PrioritizesCanonicalIdBeforeSimilarTitle()
        {
            // AC: AC-NZB-008 requires canonical NZBID matching before title fallback.
            // Behavior: ID and title target different tracked objects -> public poll -> canonical-ID object mutates.
            // @category: core-functionality
            // @lane: integration
            // @dependency: private NZBID lookup and TitleUtils.AreTitlesSimilar
            // @complexity: medium
            // Value Score: 30
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [],
                [
                    HistoryEntryValue(
                        nzbId: "401",
                        title: "Similar Book",
                        status: "SUCCESS/UNPACK",
                        finalDir: "/final/id-match")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var idMatch = CreateDownload("id-match", "Different Book", "401", 10);
            var titleMatch = CreateDownload("title-match", "Similar Book", "999", 10);

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [idMatch, titleMatch],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Completed, idMatch.Status);
            Assert.Equal("/final/id-match", idMatch.DownloadPath);
            Assert.Equal(DownloadStatus.Queued, titleMatch.Status);
        }

        [Fact]
        public async Task FetchDownloadsAsync_HistoryCanonicalId_IsNotSuppressedByActiveTitleOverlap()
        {
            // AC: AC-NZB-008 and AC-NZB-010 require ID-first resolution while active wins only its own overlap.
            // Behavior: Active ID A and history ID B have similar titles -> public poll -> both separate tracked objects update.
            // @category: integration
            // @lane: integration
            // @dependency: active private identity and history canonical NZBID lookup
            // @complexity: high
            // Value Score: 35
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [ActiveGroup(410, "Shared Book Extended", "DOWNLOADING", "other")],
                [
                    HistoryEntryValue(
                        nzbId: "411",
                        title: "Shared Book",
                        status: "SUCCESS/UNPACK",
                        finalDir: "/final/history-id")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var active = CreateDownload("active-id-a", "Shared Book Extended", "410", 100);
            var history = CreateDownload("history-id-b", "Shared Book", "411", 10);

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [active, history],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Downloading, active.Status);
            Assert.Equal(DownloadStatus.Completed, history.Status);
            Assert.Equal("/final/history-id", history.DownloadPath);
        }

        [Fact]
        public async Task FetchDownloadsAsync_HistoryMatching_UsesTitleFallbackWhenCanonicalIdDoesNotMatch()
        {
            // AC: AC-NZB-009 requires TitleUtils.AreTitlesSimilar as the only fallback after ID mismatch.
            // Behavior: No canonical ID match -> ordered title fallback -> first similar unmatched object mutates.
            // @category: core-functionality
            // @lane: integration
            // @dependency: TitleUtils.AreTitlesSimilar
            // @complexity: medium
            // Value Score: 27
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [],
                [
                    HistoryEntryValue(
                        nzbId: "999",
                        title: "Fallback_Book [MP3]",
                        status: "SUCCESS/UNPACK",
                        finalDir: "/final/title-match")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var download = CreateDownload("title-match", "Fallback Book", "402", 10);

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [download],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Completed, download.Status);
            Assert.Equal("/final/title-match", download.DownloadPath);
        }

        [Fact]
        public async Task FetchDownloadsAsync_TitleFallback_UsesFirstSimilarRemainingTrackedObject()
        {
            // AC: AC-NZB-009 and AC-NZB-020 preserve ordered title fallback without changing list shape.
            // Behavior: Multiple unmatched similar titles -> public poll -> first tracked TitleUtils match mutates.
            // @category: core-functionality
            // @lane: integration
            // @dependency: ordered tracked list and TitleUtils.AreTitlesSimilar
            // @complexity: medium
            // Value Score: 28
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [],
                [
                    HistoryEntryValue(
                        nzbId: "999",
                        title: "Shared Book Extended",
                        status: "SUCCESS/UNPACK",
                        finalDir: "/final/ordered-title")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var first = CreateDownload("first-title", "Shared Book", "510", 10);
            var second = CreateDownload("second-title", "Shared Book Extended", "511", 10);

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [first, second],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Completed, first.Status);
            Assert.Equal("/final/ordered-title", first.DownloadPath);
            Assert.Equal(DownloadStatus.Queued, second.Status);
        }

        [Fact]
        public async Task FetchDownloadsAsync_ActiveMatch_TakesPrecedenceOverOverlappingHistory()
        {
            // AC: AC-NZB-010 requires active data to win when active and history identify the same object.
            // Behavior: Same canonical ID in active and history -> public poll -> active mutation remains authoritative.
            // @category: core-functionality
            // @lane: integration
            // @dependency: active match set and history canonical NZBID lookup
            // @complexity: medium
            // Value Score: 30
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [ActiveGroup(501, "Active Priority Book", "DOWNLOADING", "other")],
                [
                    HistoryEntryValue(
                        nzbId: "501",
                        title: "Active Priority Book",
                        status: "FAILURE/UNPACK",
                        fileSizeMb: "100",
                        downloadedSizeMb: "50")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var download = CreateDownload("active-priority", "Active Priority Book", "501", 100);

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [download],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Downloading, download.Status);
            Assert.Null(download.ErrorMessage);
            Assert.False(download.Metadata.ContainsKey("ClientFailureReason"));
        }

        [Fact]
        public async Task FetchDownloadsAsync_DuplicateHistoryId_AppliesOnlyFirstQualifyingEntry()
        {
            // AC: AC-NZB-015 requires duplicate visible-history NZBIDs to apply at most once.
            // Behavior: Duplicate history ID -> public poll -> first qualifying server entry wins once.
            // @category: edge-case
            // @lane: integration
            // @dependency: typed history server order and duplicate-ID set
            // @complexity: medium
            // Value Score: 24
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [],
                [
                    HistoryEntryValue(
                        nzbId: "601",
                        title: "Duplicate Book",
                        status: "SUCCESS/UNPACK",
                        finalDir: "/final/first"),
                    HistoryEntryValue(
                        nzbId: "601",
                        title: "Duplicate Book",
                        status: "FAILURE/UNPACK")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var download = CreateDownload("duplicate", "Duplicate Book", "601", 10);

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [download],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Completed, download.Status);
            Assert.Equal("/final/first", download.DownloadPath);
            Assert.Null(download.ErrorMessage);
        }

        [Fact]
        public async Task FetchDownloadsAsync_ConfiguredCategory_FiltersHistoryButNotActive()
        {
            // AC: AC-NZB-012 preserves unfiltered active polling and filters configured history only.
            // Behavior: Mixed active/history categories -> public poll -> active updates and only matching history mutates.
            // @category: core-functionality
            // @lane: integration
            // @dependency: DownloadClientCategoryFilter
            // @complexity: medium
            // Value Score: 29
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [ActiveGroup(701, "Unfiltered Active", "DOWNLOADING", "other")],
                [
                    HistoryEntryValue(
                        nzbId: "702",
                        title: "Filtered History",
                        status: "SUCCESS/UNPACK",
                        category: "other",
                        finalDir: "/final/filtered"),
                    HistoryEntryValue(
                        nzbId: "703",
                        title: "Matching History",
                        status: "SUCCESS/UNPACK",
                        category: " AUDIOBOOKS ",
                        finalDir: "/final/matching")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var client = CreateClient();
            client.Settings = new Dictionary<string, object> { ["category"] = "audiobooks" };
            var active = CreateDownload("active", "Unfiltered Active", "701", 10);
            var filtered = CreateDownload("filtered", "Filtered History", "702", 10);
            var matching = CreateDownload("matching", "Matching History", "703", 10);

            await adapter.FetchDownloadsAsync(
                client,
                [active, filtered, matching],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Downloading, active.Status);
            Assert.Equal(DownloadStatus.Queued, filtered.Status);
            Assert.Equal(DownloadStatus.Completed, matching.Status);
        }

        [Fact]
        public async Task FetchDownloadsAsync_ActiveTitleFallback_PreservesExistingSimilarityBehavior()
        {
            // AC: AC-NZB-001 preserves existing active title fallback behavior.
            // Behavior: Active ID mismatch with similar title -> public poll -> active progress still updates.
            // @category: core-functionality
            // @lane: integration
            // @dependency: TitleUtils.AreTitlesSimilar
            // @complexity: medium
            // Value Score: 26
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [
                    ActiveGroup(
                        704,
                        "The Great Adventure by John Smith",
                        "DOWNLOADING",
                        "other")
                ],
                []);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var download = CreateDownload(
                "active-title",
                "The Great Adventure",
                "different-id",
                100);

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [download],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Downloading, download.Status);
            Assert.Equal(0.75m, download.Progress);
        }

        [Theory]
        [InlineData("WARNING/REPAIRABLE")]
        [InlineData("DELETED/MANUAL")]
        [InlineData("")]
        [InlineData("UNKNOWN/FUTURE")]
        public async Task FetchDownloadsAsync_IgnoredHistoryStatus_DoesNotMutateTrackedDownload(
            string status)
        {
            // AC: AC-NZB-005 requires warning, deleted, empty, and unknown history statuses to be ignored.
            // Behavior: Non-terminal history status -> public poll -> tracked object remains unchanged.
            // @category: edge-case
            // @lane: integration
            // @dependency: typed history outcome classification
            // @complexity: low
            // Value Score: 22
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [],
                [
                    HistoryEntryValue(
                        nzbId: "801",
                        title: "Ignored Book",
                        status: status,
                        finalDir: "/final/ignored")
                ]);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            var download = CreateDownload("ignored", "Ignored Book", "801", 10);

            await adapter.FetchDownloadsAsync(
                CreateClient(),
                [download],
                CancellationToken.None);

            Assert.Equal(DownloadStatus.Queued, download.Status);
            Assert.Equal(string.Empty, download.DownloadPath);
        }

        [Fact]
        public async Task FetchDownloadsAsync_HistoryCancellation_PropagatesOperationCanceledException()
        {
            // AC: AC-NZB-013 requires cancellation to propagate unchanged through history polling.
            // Behavior: Cancellation during history request -> public poll -> OperationCanceledException escapes.
            // @category: edge-case
            // @lane: integration
            // @dependency: cancellation-aware XML-RPC history reader
            // @complexity: medium
            // Value Score: 30
            using var apiMock = new NzbgetApiMock();
            QueueJsonPollingResponses(apiMock, []);
            apiMock.QueueXmlRpcResponse(
                "history",
                NzbgetApiMock.CreateHistoryResponse(string.Empty),
                HttpStatusCode.OK,
                TimeSpan.FromSeconds(5));
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);
            using var cancellationTokenSource = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => adapter.FetchDownloadsAsync(
                    CreateClient(),
                    [],
                    cancellationTokenSource.Token));
        }

        [Theory]
        [InlineData("malformed")]
        [InlineData("authentication")]
        [InlineData("fault")]
        public async Task FetchDownloadsAsync_HistoryFailure_WrapsNonCancellationFailure(
            string failureKind)
        {
            // AC: AC-NZB-014 requires malformed/auth/fault history failures to fail polling explicitly.
            // Behavior: Non-cancellation history boundary failure -> public poll -> contextual polling exception.
            // @category: edge-case
            // @lane: integration
            // @dependency: XML-RPC HTTP/status/fault parsing
            // @complexity: medium
            // Value Score: 30
            using var apiMock = new NzbgetApiMock();
            QueueJsonPollingResponses(apiMock, []);
            var (body, statusCode) = failureKind switch
            {
                "malformed" => ("<not-xml", HttpStatusCode.OK),
                "authentication" => ("unauthorized", HttpStatusCode.Unauthorized),
                "fault" => (
                    """
                    <?xml version="1.0"?>
                    <methodResponse>
                      <fault>
                        <value><struct>
                          <member><name>faultString</name><value><string>Denied</string></value></member>
                        </struct></value>
                      </fault>
                    </methodResponse>
                    """,
                    HttpStatusCode.OK),
                _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
            };
            apiMock.QueueXmlRpcResponse(
                "history",
                body,
                statusCode,
                TimeSpan.Zero);
            using var http = new HttpClient(apiMock);
            var adapter = CreateAdapter(http);

            var exception = await Assert.ThrowsAsync<DownloadClientAdapterPollingException>(
                () => adapter.FetchDownloadsAsync(
                    CreateClient(),
                    [],
                    CancellationToken.None));

            Assert.NotNull(exception.InnerException);
            Assert.IsNotType<OperationCanceledException>(exception.InnerException);
        }

        [Fact]
        public async Task FetchDownloadsAsync_FastHistory_LogsOneMeasurementAndNoSlowWarning()
        {
            // AC: Rollback observability requires one sanitized measurement and no warning at 2000ms.
            // Behavior: History duration equals threshold -> public poll -> one DEBUG measurement and zero WARNING events.
            // @category: edge-case
            // @lane: integration
            // @dependency: injected TimeProvider and structured ILogger
            // @complexity: medium
            // Value Score: 24
            using var apiMock = new NzbgetApiMock();
            QueuePollingResponses(
                apiMock,
                [],
                [HistoryEntryValue("901", "Measured Book", "WARNING/REPAIRABLE")]);
            using var http = new HttpClient(apiMock);
            var logger = new CapturingLogger<NzbgetAdapter>();
            var adapter = CreateAdapter(
                http,
                logger,
                new SequenceTimeProvider(0, 2_000));
            var client = CreateClient();
            client.Id = "client\nid";

            await adapter.FetchDownloadsAsync(client, [], CancellationToken.None);

            var measurement = Assert.Single(
                logger.Entries,
                entry => entry.Level == LogLevel.Debug &&
                    GetLogValue(entry, "Surface") == "FetchDownloadsAsync");
            Assert.Equal("client id", GetLogValue(measurement, "ClientId"));
            Assert.Equal("1", GetLogValue(measurement, "HistoryCount"));
            Assert.Equal("2000", GetLogValue(measurement, "ElapsedMs"));
            Assert.DoesNotContain(
                logger.Entries,
                entry => entry.Level == LogLevel.Warning &&
                    GetLogValue(entry, "Surface") == "FetchDownloadsAsync");
        }

        [Fact]
        public async Task FetchDownloadsAsync_SlowHistory_LogsSanitizedWarningWithExactFields()
        {
            // AC: Rollback observability requires a sanitized warning only when history duration exceeds 2000ms.
            // Behavior: History duration is 2001ms -> public poll -> exact structured DEBUG and WARNING fields.
            // @category: edge-case
            // @lane: integration
            // @dependency: injected TimeProvider, LogRedaction, and structured ILogger
            // @complexity: medium
            // Value Score: 25
            using var apiMock = new NzbgetApiMock();
            QueueJsonPollingResponses(apiMock, []);
            apiMock.QueueXmlRpcResponse(
                "history",
                NzbgetApiMock.CreateHistoryResponse(
                    HistoryEntryValue("902", "Slow Book", "WARNING/REPAIRABLE")),
                HttpStatusCode.OK,
                TimeSpan.Zero);
            using var http = new HttpClient(apiMock);
            var logger = new CapturingLogger<NzbgetAdapter>();
            var adapter = CreateAdapter(
                http,
                logger,
                new SequenceTimeProvider(0, 2_001));
            var client = CreateClient();
            client.Id = "client\r\nid";

            await adapter.FetchDownloadsAsync(client, [], CancellationToken.None);

            var warning = Assert.Single(
                logger.Entries,
                entry => entry.Level == LogLevel.Warning &&
                    GetLogValue(entry, "Surface") == "FetchDownloadsAsync");
            Assert.Equal("client  id", GetLogValue(warning, "ClientId"));
            Assert.Equal("1", GetLogValue(warning, "HistoryCount"));
            Assert.Equal("2001", GetLogValue(warning, "ElapsedMs"));
            Assert.Single(
                logger.Entries,
                entry => entry.Level == LogLevel.Debug &&
                    GetLogValue(entry, "Surface") == "FetchDownloadsAsync");
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
            return CreateAdapter(http, NullLogger<NzbgetAdapter>.Instance);
        }

        private static NzbgetAdapter CreateAdapter(
            HttpClient http,
            ILogger<NzbgetAdapter> logger,
            TimeProvider? timeProvider = null)
        {
            return new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                logger,
                timeProvider ?? TimeProvider.System);
        }

        private static NzbgetHistoryReader CreateHistoryReader(HttpClient http)
        {
            return new NzbgetHistoryReader(
                new NzbgetXmlRpcClient(new TestHttpClientFactory(http), "nzbget"));
        }

        private static MergeHistoryDelegate CreateMergeHistoryDelegate()
        {
            var mergeMethod = typeof(NzbgetDownloadPollingWorkflow).GetMethod(
                "MergeHistory",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "NzbgetDownloadPollingWorkflow.MergeHistory was not found.");
            return mergeMethod.CreateDelegate<MergeHistoryDelegate>();
        }

        private static XElement CreatePerformanceHistoryResult()
        {
            var entries = new string[PerformanceHistoryEntryCount];
            for (var index = 0; index < entries.Length; index++)
            {
                var canonicalId = PerformanceHistoryId(
                    index == 996
                        ? 992
                        : index);
                var status = (index % 4) switch
                {
                    0 => "SUCCESS/UNPACK",
                    1 => "FAILURE/HEALTH",
                    2 => "WARNING/REPAIRABLE",
                    _ => "DELETED/MANUAL"
                };
                entries[index] = HistoryEntryValue(
                    nzbId: canonicalId,
                    title: $"Performance Book {index}",
                    status: status,
                    category: "audiobooks",
                    finalDir: $"/final/performance-{index}",
                    destDir: $"/destination/performance-{index}",
                    fileSizeMb: "100",
                    downloadedSizeMb: index % 4 == 1 ? "60" : "100");
            }

            return XElement.Parse(
                $"<value><array><data>{string.Concat(entries)}</data></array></value>");
        }

        private static (
            DownloadClientConfiguration Client,
            List<Download> Downloads,
            IReadOnlyDictionary<string, Download> TrackedById,
            HashSet<Download> MatchedDownloads,
            HashSet<string> ActiveCanonicalIds,
            Download BeyondOneHundredDownload)
            CreatePerformanceMergeState()
        {
            var client = CreateClient();
            var downloads = new List<Download>(PerformanceHistoryEntryCount);
            var trackedById = new Dictionary<string, Download>(
                PerformanceHistoryEntryCount,
                StringComparer.OrdinalIgnoreCase);
            var matchedDownloads = new HashSet<Download>();
            var activeCanonicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < PerformanceHistoryEntryCount; index++)
            {
                var canonicalId = PerformanceHistoryId(index);
                var download = CreateDownload(
                    $"performance-{index}",
                    $"Performance Book {index}",
                    canonicalId,
                    100);
                downloads.Add(download);
                trackedById.TryAdd(canonicalId, download);

                if (index % 20 == 0)
                {
                    download.Status = DownloadStatus.Downloading;
                    matchedDownloads.Add(download);
                    activeCanonicalIds.Add(canonicalId);
                }
            }

            return (
                client,
                downloads,
                trackedById,
                matchedDownloads,
                activeCanonicalIds,
                downloads[PerformanceBeyondOneHundredIndex]);
        }

        private static string PerformanceHistoryId(int index)
        {
            return (10_000 + index).ToString(CultureInfo.InvariantCulture);
        }

        private static DownloadClientConfiguration CreateClient()
        {
            return new DownloadClientConfiguration
            {
                Host = "localhost",
                Port = 6789
            };
        }

        private static Download CreateDownload(
            string id,
            string title,
            string externalId,
            long totalSizeMb)
        {
            var download = new Download
            {
                Id = id,
                Title = title,
                TotalSize = totalSizeMb * 1024 * 1024
            };
            download.SetExternalId(externalId);
            return download;
        }

        private static object ActiveGroup(
            int nzbId,
            string title,
            string status,
            string category)
        {
            return new
            {
                NZBID = nzbId,
                NZBName = title,
                Status = status,
                Category = category,
                FileSizeMB = "100",
                RemainingSizeMB = "25"
            };
        }

        private static void QueuePollingResponses(
            NzbgetApiMock apiMock,
            IReadOnlyList<object> activeGroups,
            IReadOnlyList<string> historyEntries)
        {
            QueueJsonPollingResponses(apiMock, activeGroups);
            apiMock.QueueXmlRpcResponse(
                "history",
                NzbgetApiMock.CreateHistoryResponse(string.Concat(historyEntries)));
        }

        private static void QueueJsonPollingResponses(
            NzbgetApiMock apiMock,
            IReadOnlyList<object> activeGroups)
        {
            apiMock.QueueJsonRpcResponse("status", """{"result":{},"id":2}""");
            apiMock.QueueJsonRpcResponse(
                "listgroups",
                JsonSerializer.Serialize(new { result = activeGroups, id = 3 }));
        }

        private static string GetLogValue(CapturedLog entry, string key)
        {
            return entry.State.TryGetValue(key, out var value)
                ? value?.ToString() ?? string.Empty
                : string.Empty;
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
