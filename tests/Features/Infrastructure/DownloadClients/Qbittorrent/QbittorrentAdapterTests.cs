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
using System.Text.Json;
using System.Web;
using Listenarr.Domain.Downloads.Exceptions;
using Listenarr.Tests.Common;

using Listenarr.Tests.Builders;
using Listenarr.Tests.Mocks.Api;
using Listenarr.Infrastructure.Torrents;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent
{
    public class QbittorrentAdapterTests : BaseTests
    {
        private DownloadClientConfiguration _client = null!;

        public override async Task InitializeAsync()
        {
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(443)
                .WithSsl()
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());
        }

        [Fact]
        public async Task TestConnection_When_VersionForbidden_Then_LoginSucceeds_ReturnsSuccess()
        {
            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var (success, message) = await adapter.TestConnectionAsync(_client);

            Assert.True(success);
            Assert.Contains("Successfully connected to qBittorrent", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnection_When_VersionForbidden_And_NoCredentials_ReturnsForbidden()
        {
            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(443)
                .WithSsl()
                .WithType("qbittorrent")
                .Build());

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.False(success);
            Assert.Contains("Forbidden", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnection_NormalizesHostWithSchemeAndPath()
        {
            var mock = _provider.GetRequiredService<QbittorrentApiMock>();

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("192.168.50.111")
                .WithPort(8080)
                .WithoutSsl()
                .WithType("qbittorrent")
                .WithUsername("admin")
                .WithPassword("admin")
                .Build());

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.Contains("Successfully connected to qBittorrent", message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(mock.GetLastRequest());

            var uri = mock.GetLastRequest().RequestUri;
            Assert.Equal("http", uri.Scheme);
            Assert.Equal("192.168.50.111", uri.Host);
            Assert.Equal(8080, uri.Port);
            Assert.Equal("/api/v2/app/version", uri.AbsolutePath);
        }

        [Fact]
        public async Task AddAsync_WhenMagnetAndTorrentUrlAreProvided_UsesVerifiedMagnetHashWithoutDownloading()
        {
            var downloader = new Mock<ITorrentFileDownloader>(MockBehavior.Strict);
            _services.AddSingleton(downloader.Object);
            Init();

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12",
                TorrentUrl = "https://indexer.example.com/book.torrent"
            };

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var submissionResult = await adapter.AddAsync(
                client,
                PreparedSubmissionTestFactory.Torrent(searchResult));

            Assert.Equal("ABCDEF1234567890ABCDEF1234567890ABCDEF12", submissionResult.ExternalId);
            downloader.Verify(
                x => x.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task AddAsync_WhenClientAlreadyHoldsTheRelease_ThrowsRejectionNotSubmissionFailure()
        {
            // qBittorrent 5.2 answers 409 when the info-hash is already in its download list.
            // That happens whenever one release satisfies more than one wanted book, and it
            // means "I have this", not "the client is broken", so it has to be tellable apart
            // from a genuine submission failure by type.
            Init();
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.AddStatusCode = HttpStatusCode.Conflict;
            apiMock.AddResponseBody = "Torrent is already in the download list.";

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12"
            };

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();

            await Assert.ThrowsAsync<DownloadClientRejectedReleaseException>(
                () => adapter.AddAsync(client, PreparedSubmissionTestFactory.Torrent(searchResult)));
        }

        [Fact]
        public async Task AddAsync_WhenClientFailsForAnotherReason_StaysAPlainSubmissionFailure()
        {
            // The control for the test above. If the new branch were widened to any failure
            // status, this would start reporting a rejection and a genuinely broken client
            // would be silently skipped instead of surfaced.
            Init();
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.AddStatusCode = HttpStatusCode.InternalServerError;

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12"
            };

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();

            var exception = await Assert.ThrowsAsync<DownloadClientSubmissionException>(
                () => adapter.AddAsync(client, PreparedSubmissionTestFactory.Torrent(searchResult)));

            Assert.IsNotType<DownloadClientRejectedReleaseException>(exception);
        }

        [Fact]
        public async Task AddAsync_WhenMagnetUsesBase32Hash_ReturnsNormalizedHexHash()
        {
            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:AERUKZ4JVPG66AJDIVTYTK6N54ASGRLH"
            };

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var submissionResult = await adapter.AddAsync(
                client,
                PreparedSubmissionTestFactory.Torrent(searchResult));

            Assert.Equal("0123456789ABCDEF0123456789ABCDEF01234567", submissionResult.ExternalId);
        }

        [Fact]
        public async Task AddAsync_WhenTorrentDownloadFails_DoesNotCallQbittorrentAdd()
        {
            var downloader = new Mock<ITorrentFileDownloader>(MockBehavior.Strict);
            downloader
                .Setup(x => x.DownloadAsync("https://indexer.example.com/book.torrent", It.IsAny<CancellationToken>()))
                .ReturnsAsync(TorrentDownloadResult.Failed("Torrent metadata download failed with HTTP 500."));
            _services.AddSingleton(downloader.Object);
            Init();

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                TorrentUrl = "https://indexer.example.com/book.torrent"
            };

            var exception = await Assert.ThrowsAsync<DownloadClientSubmissionException>(
                () => new GenericTorrentSourceResolver(downloader.Object, new TorrentMetadataService())
                    .ResolveAsync(
                        TrustedDownloadCandidateFactory.Create(searchResult),
                        null,
                        CancellationToken.None));

            Assert.Contains("HTTP 500", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, _provider.GetRequiredService<QbittorrentApiMock>().GetCallCount());
        }

        [Fact]
        public async Task AddAsync_WhenTorrentUrlReturnsBytes_ComputesHashBeforeSubmission()
        {
            var content = await File.ReadAllBytesAsync(TestUtils.GetTorrentDataPath("big-buck-bunny.torrent"));
            var downloader = new Mock<ITorrentFileDownloader>(MockBehavior.Strict);
            downloader
                .Setup(x => x.DownloadAsync("https://indexer.example.com/book.torrent", It.IsAny<CancellationToken>()))
                .ReturnsAsync(TorrentDownloadResult.FromBytes(content));
            _services.AddSingleton(downloader.Object);
            Init();

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                TorrentUrl = "https://indexer.example.com/book.torrent"
            };

            var prepared = await new GenericTorrentSourceResolver(downloader.Object, new TorrentMetadataService())
                .ResolveAsync(TrustedDownloadCandidateFactory.Create(searchResult), null, CancellationToken.None);
            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var submissionResult = await adapter.AddAsync(client, prepared);

            Assert.Equal("DD8255ECDC7CA55FB0BBF81323D87062DB1F6D1C", submissionResult.ExternalId);
            Assert.True(_provider.GetRequiredService<QbittorrentApiMock>().GetCallCount() >= 2);
        }

        [Fact]
        public async Task AddAsync_WhenTorrentBytesAreInvalid_DoesNotCallQbittorrentAdd()
        {
            var downloader = new Mock<ITorrentFileDownloader>(MockBehavior.Strict);
            downloader
                .Setup(x => x.DownloadAsync("https://indexer.example.com/book.torrent", It.IsAny<CancellationToken>()))
                .ReturnsAsync(TorrentDownloadResult.FromBytes("d3:foo3:bar"u8.ToArray()));
            _services.AddSingleton(downloader.Object);
            Init();

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            await Assert.ThrowsAsync<DownloadClientSubmissionException>(
                () => new GenericTorrentSourceResolver(downloader.Object, new TorrentMetadataService())
                    .ResolveAsync(
                        TrustedDownloadCandidateFactory.Create(new SearchResult
                        {
                            Title = "Book",
                            TorrentUrl = "https://indexer.example.com/book.torrent"
                        }),
                        null,
                        CancellationToken.None));

            Assert.Equal(0, _provider.GetRequiredService<QbittorrentApiMock>().GetCallCount());
        }

        [Fact]
        public async Task AddAsync_WhenTorrentUrlUsesInvalidScheme_ThrowsArgumentException()
        {
            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var searchResult = new SearchResult
            {
                Title = "Book",
                TorrentUrl = "ftp://indexer.example.com/book.torrent"
            };

            var downloader = new Mock<ITorrentFileDownloader>();
            downloader.Setup(value => value.DownloadAsync(searchResult.TorrentUrl, It.IsAny<CancellationToken>()))
                .ReturnsAsync(TorrentDownloadResult.Failed("The torrent URL was rejected by outbound request validation."));
            var exception = await Assert.ThrowsAsync<DownloadClientSubmissionException>(() =>
                new GenericTorrentSourceResolver(downloader.Object, new TorrentMetadataService())
                    .ResolveAsync(
                        TrustedDownloadCandidateFactory.Create(searchResult),
                        null,
                        CancellationToken.None));

            Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "SingleFileResolvesContentFilePath")]
        public async Task GetImportItemAsync_SingleFileTorrent_ResolvesSpecificFilePath()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads", "audiobooks");

            var files = ParseFiles("[{\"name\":\"Book.m4b\"}]");
            var resolvedPath = QbittorrentAdapter.ResolveTorrentContentPath(savePath, files);

            var expectedPath = FileUtils.CombineWithOptionalBase(savePath, "Book.m4b");
            Assert.Equal(expectedPath, FileUtils.NormalizeStoredPath(resolvedPath));
            await Task.CompletedTask;
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "MultiFileResolvesTopLevelDirectory")]
        public async Task GetImportItemAsync_MultiFileTorrent_ResolvesTopLevelFolderPath()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads", "audiobooks");

            var files = ParseFiles("[{\"name\":\"Series Book/file1.m4b\"},{\"name\":\"Series Book/file2.m4b\"}]");

            var resolvedPath = QbittorrentAdapter.ResolveTorrentContentPath(savePath, files);

            var expectedPath = FileUtils.CombineWithOptionalBase(savePath, "Series Book");
            Assert.Equal(expectedPath, FileUtils.NormalizeStoredPath(resolvedPath));
            await Task.CompletedTask;
        }

        [Theory]
        [InlineData(0.5, -1f, null, -1, false, -1f, false, -1, true)]
        [InlineData(1.0, 1.0f, null, -1, false, -1f, false, -1, true)]
        [InlineData(0.9995, 1.0f, null, -1, false, -1f, false, -1, true)]
        [InlineData(0.5, 1.0f, null, -1, false, -1f, false, -1, false)]
        [InlineData(1.5, -2f, null, -1, true, 1.5f, false, -1, true)]
        [InlineData(0.5, -2f, null, -1, true, 1.5f, false, -1, false)]
        [InlineData(0.5, -1f, 3600, 60, false, -1f, false, -1, true)]
        [InlineData(0.5, -1f, 3599, 60, false, -1f, false, -1, false)]
        [InlineData(0.5, -1f, 7200, -2, false, -1f, true, 120, true)]
        [InlineData(0.5, -1f, 7199, -2, false, -1f, true, 120, false)]
        public void HasReachedSeedLimit_EvaluatesQbittorrentRatioAndSeedingTimePolicy(
            double ratio,
            float ratioLimit,
            int? seedingTime,
            long seedingTimeLimit,
            bool globalMaxRatioEnabled,
            float globalMaxRatio,
            bool globalMaxSeedingTimeEnabled,
            long globalMaxSeedingTime,
            bool expected)
        {
            var result = QbittorrentSeedLimitEvaluator.HasReachedSeedLimit(
                ratio,
                ratioLimit,
                seedingTime,
                seedingTimeLimit,
                globalMaxRatioEnabled,
                globalMaxRatio,
                globalMaxSeedingTimeEnabled,
                globalMaxSeedingTime);

            Assert.Equal(expected, result);
        }

        [Fact]
        [Trait("Area", "QbittorrentImportPathResolution")]
        [Trait("Scenario", "LocalAutoImportKeepsExistingPath")]
        public async Task GetImportItemAsync_PrepopulatedContentPath_KeepsLocalPath_ForNonDockerAutoImport()
        {
            string localPath = FileUtils.GetAbsolutePath("media", "downloads", "Stephen King", "It.m4b");

            using var http = new HttpClient(new DelegatingHandlerMock((_, _) =>
                throw new InvalidOperationException("HTTP should not be called when qBittorrent content_path is already available.")));

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(8080)
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());

            var queueItem = new QueueItem
            {
                Id = "dl-qbit-local",
                Title = "It",
                Status = "completed",
                ContentPath = localPath,
                DownloadClientId = client.Id
            };

            var adapter = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var qbittorrentAdapter = (QbittorrentAdapter)adapter.ResolveAdapter(client);
            var resolved = await qbittorrentAdapter.GetImportItemAsync(client, new Download { Id = queueItem.Id }, queueItem);

            Assert.Equal(localPath, resolved.ContentPath);
        }

        private static List<Dictionary<string, JsonElement>> ParseFiles(string json)
        {
            var root = JsonDocument.Parse(json).RootElement;
            var files = new List<Dictionary<string, JsonElement>>();

            foreach (var element in root.EnumerateArray())
            {
                var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = property.Value;
                }
                files.Add(map);
            }

            return files;
        }

        [Fact]
        public async Task AddAsync_ComputeHash_FromTorrentFile()
        {
            var filePath = TestUtils.GetTorrentDataPath("big-buck-bunny.torrent");
            var content = await File.ReadAllBytesAsync(filePath);

            var searchResult = new SearchResultBuilder()
                .WithTorrentData(content)
                .Build();

            var adapter = _provider.GetRequiredService<IDownloadClientGateway>();
            var submissionResult = await adapter.AddAsync(
                _client,
                PreparedSubmissionTestFactory.Torrent(searchResult));

            Assert.Equal("DD8255ECDC7CA55FB0BBF81323D87062DB1F6D1C", submissionResult.ExternalId);
        }

        [Fact]
        public async Task GetQueueAsync_WithIds_AddsHashesQuery()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = """
            [
                {
                    "hash": "abcdef",
                    "name": "Book",
                    "progress": 0.5,
                    "size": 1000,
                    "downloaded": 500,
                    "state": "downloading",
                    "save_path": "/downloads/book"
                }
            ]
            """;
            apiMock.ResetRequestHistory();
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client, ["ABCDEF", "123456"]);

            Assert.NotEmpty(items);
            var infoRequest = Assert.Single(apiMock.RequestHistory,
                request => request.RequestUri.AbsolutePath.EndsWith("/api/v2/torrents/info", StringComparison.Ordinal));
            var query = HttpUtility.ParseQueryString(infoRequest.RequestUri.Query);
            Assert.Equal("abcdef|123456", query["hashes"]);
        }

        [Fact]
        public async Task GetQueueAsync_WithoutIds_DoesNotAddHashesQuery()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.ResetRequestHistory();
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client);

            Assert.NotEmpty(items);
            var infoRequest = Assert.Single(apiMock.RequestHistory,
                request => request.RequestUri.AbsolutePath.EndsWith("/api/v2/torrents/info", StringComparison.Ordinal));
            var query = HttpUtility.ParseQueryString(infoRequest.RequestUri.Query);
            Assert.Null(query["hashes"]);
        }

        [Fact]
        public async Task GetQueueAsync_WithIds_ThrowsPollingException_OnQueueRequestFailure()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoStatusCode = HttpStatusCode.InternalServerError;
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            await Assert.ThrowsAsync<DownloadClientAdapterPollingException>(
                () => adapter.GetQueueAsync(_client, ["ABCDEF"]));
        }

        [Fact]
        public async Task GetQueueAsync_WithoutIds_ReturnsEmpty_OnQueueRequestFailure()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoStatusCode = HttpStatusCode.InternalServerError;
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client);

            Assert.Empty(items);
        }

        [Fact]
        public async Task MarkItemAsImportedAsync_SetsConfiguredPostImportCategory()
        {
            _client.Settings = new Dictionary<string, object>
            {
                ["postImportCategory"] = "listenarr-imported"
            };
            await _downloadClientConfigurationRepository.SaveAsync(_client);
            var download = new DownloadBuilder()
                .WithDownloadClientConfiguration(_client)
                .WithClientDownloadId("ABCDEF123456")
                .Build();

            var gateway = _provider.GetRequiredService<IDownloadClientGateway>();
            var result = await gateway.MarkItemAsImportedAsync(_client, download);

            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            Assert.True(result, $"Last request: {apiMock.GetLastRequest().RequestUri}; content: {apiMock.GetLastContent()}");
            var form = apiMock.LastCategoryForm;
            Assert.NotNull(form);
            Assert.Equal("abcdef123456", form!["hashes"]);
            Assert.Equal("listenarr-imported", form["category"]);
        }

        [Theory]
        [InlineData(false, "false")]
        [InlineData(true, "true")]
        public async Task RemoveAsync_PreservesDeleteFilesPolicy(bool deleteFiles, string expected)
        {
            var gateway = _provider.GetRequiredService<IDownloadClientGateway>();

            var result = await gateway.RemoveAsync(_client, "ABCDEF123456", deleteFiles);

            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            Assert.True(result, $"Last request: {apiMock.GetLastRequest().RequestUri}; content: {apiMock.GetLastContent()}");
            var form = apiMock.LastDeleteForm;
            Assert.NotNull(form);
            Assert.Equal("ABCDEF123456", form!["hashes"]);
            Assert.Equal(expected, form["deleteFiles"]);
        }

        [Theory]
        [InlineData("uploading")]
        [InlineData("stalledUP")]
        [InlineData("stoppedUP")]
        public void CompletedTorrentStates_MapToCompleted(string state)
        {
            Assert.Equal(DownloadItemStatus.Completed, QbittorrentResponseMapper.MapDownloadItemStatus(state, 100));
            Assert.Equal("completed", QbittorrentResponseMapper.MapQueueStatus(state, 100));
        }
    }
}
