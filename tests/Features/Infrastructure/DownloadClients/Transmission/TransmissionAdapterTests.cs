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
using System.Runtime.InteropServices;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;

using Listenarr.Infrastructure.Torrents;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Transmission
{
    [Trait("Name", "TransmissionAdapterTests")]
    [Trait("Category", "DownloadClientAdapter")]
    [Trait("Third-Party", "Transmission")]
    public class TransmissionAdapterTests : BaseTests
    {
        private readonly string CLIENT_CONFIG_ID = "slskd-1";

        private DownloadClientConfiguration? _client;

        public override async Task InitializeAsync()
        {
            await InitDataAsync();
        }

        private async Task InitDataAsync()
        {
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId(CLIENT_CONFIG_ID)
                .WithName("Transmission")
                .WithType("transmission")
                .WithHost("localhost")
                .WithPort(9091)
                .Build());
        }

        [Fact]
        [Trait("Method", "AddAsync")]
        [Trait("Area", "TransmissionAdd")]
        [Trait("Scenario", "PreservesEncodedTrackerSeparatorsWhenNormalizingMagnetUri")]
        public async Task AddAsync_MagnetWithEncodedTrackerSeparators_DoesNotCorruptTrackerQuery()
        {
            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890&tr=http%3A%2F%2Ftracker.example.com%2Fannounce%3Ffoo%3D1%26bar%3D2&dn=Book%20Title"
            };

            var adapter = MockUtils.CreateTransmissionAdapter(_provider);
            var added = await adapter.AddAsync(
                _client,
                PreparedSubmissionTestFactory.Torrent(
                    searchResult.Title,
                    "ABCDEF1234567890ABCDEF1234567890ABCDEF12",
                    magnet: searchResult.MagnetLink));

            var transmissionApiMock = _provider.GetRequiredService<TransmissionApiMock>();
            using var document = transmissionApiMock.GetLastJsonContent();
            Assert.NotNull(document);
            var postedFilename = document.RootElement.GetProperty("arguments").GetProperty("filename").GetString();

            Assert.Equal("HASH1", added.ExternalId);
            Assert.Equal(
                "magnet:?xt=urn:btih:ABCDEF1234567890&tr=http%3A%2F%2Ftracker.example.com%2Fannounce%3Ffoo%3D1%26bar%3D2&dn=Book Title",
                postedFilename);
        }

        [Fact]
        [Trait("Method", "TestConnectionAsync")]
        public async Task TestConnectionAsync_NormalizesHostAndRespectsConfiguredRpcPath()
        {
            var transmissionApiMock = _provider.GetRequiredService<TransmissionApiMock>();

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("tr-1")
                .WithName("Transmission")
                .WithType("transmission")
                .WithHost("http://192.168.50.111:9999/legacy")
                .WithPort(9091)
                .WithSsl()
                .WithUrlBase("/rpc")
                .Build());

            var adapter = MockUtils.CreateTransmissionAdapter(_provider);
            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.Contains("connected", message, StringComparison.OrdinalIgnoreCase);

            var capturedRequest = transmissionApiMock.GetLastRequest();
            Assert.NotNull(capturedRequest);
            Assert.NotNull(capturedRequest.RequestUri);
            Assert.Equal("https", capturedRequest.RequestUri.Scheme);
            Assert.Equal("192.168.50.111", capturedRequest.RequestUri.Host);
            Assert.Equal(9091, capturedRequest.RequestUri.Port);
            Assert.Equal("/rpc", capturedRequest.RequestUri.AbsolutePath);
        }

        [Fact]
        [Trait("Method", "AddAsync")]
        public async Task AddAsync_WhenMagnetAndTorrentUrlAreProvided_PredownloadsTorrentUrlFirst()
        {
            string? downloadedUrl = null;
            var downloader = new Mock<ITorrentFileDownloader>(MockBehavior.Strict);
            downloader
                .Setup(x => x.DownloadAsync("https://indexer.example.com/book.torrent", It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((url, _) => downloadedUrl = url)
                .ReturnsAsync(TorrentDownloadResult.FromBytes("de"u8.ToArray()));

            var searchResult = new SearchResult
            {
                Title = "Book",
                MagnetLink = "magnet:?xt=urn:btih:ABCDEF1234567890",
                TorrentUrl = "https://indexer.example.com/book.torrent"
            };

            var adapter = MockUtils.CreateTransmissionAdapter(_provider, downloader);
            var added = await adapter.AddAsync(
                _client,
                PreparedSubmissionTestFactory.Torrent(
                    searchResult.Title,
                    "ABCDEF1234567890ABCDEF1234567890ABCDEF12",
                    bytes: "de"u8.ToArray(),
                    magnet: searchResult.MagnetLink));

            Assert.Equal("HASH1", added.ExternalId);
            Assert.Null(downloadedUrl);
            downloader.Verify(x => x.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            var transmissionApiMock = _provider.GetRequiredService<TransmissionApiMock>();
            using var document = transmissionApiMock.GetLastJsonContent();
            Assert.NotNull(document);
            var arguments = document.RootElement.GetProperty("arguments");
            var metainfo = arguments.TryGetProperty("metainfo", out var metainfoProp) ? metainfoProp.GetString() : null;
            Assert.Equal(Convert.ToBase64String("de"u8.ToArray()), metainfo);
        }

        [Fact]
        [Trait("Method", "AddAsync")]
        public async Task AddAsync_WhenTorrentUrlUsesInvalidScheme_ThrowsArgumentException()
        {
            _services.AddHttpClient("transmission")
                .ConfigurePrimaryHttpMessageHandler(() => new DelegatingHandlerMock((_, _) =>
                throw new InvalidOperationException("Network should not be hit for invalid torrent URLs.")));
            Init();
            await InitDataAsync();

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

        [Theory]
        [InlineData(true, false, 0.5, 2, 0, 2, 0, 0, false, 0, false, 0, true)]
        [InlineData(true, false, 1.5, 1, 1.5, 2, 0, 0, false, 0, false, 0, true)]
        [InlineData(false, true, 1.5, 1, 1.5, 2, 0, 0, false, 0, false, 0, false)]
        [InlineData(true, false, 1.5, 0, 0, 2, 0, 0, true, 1.5, false, 0, true)]
        [InlineData(true, false, 0.5, 0, 0, 2, 0, 0, true, 1.5, false, 0, false)]
        [InlineData(false, true, 0.5, 2, 0, 1, 60, 3601, false, 0, false, 0, true)]
        [InlineData(false, true, 0.5, 2, 0, 1, 60, 3600, false, 0, false, 0, false)]
        [InlineData(true, false, 0.5, 2, 0, 0, 0, 0, false, 0, true, 60, true)]
        public void HasReachedSeedLimit_EvaluatesTransmissionRatioAndIdlePolicy(
            bool isStopped,
            bool isSeeding,
            double ratio,
            int seedRatioMode,
            double seedRatioLimit,
            int seedIdleMode,
            int seedIdleLimit,
            long secondsSeeding,
            bool sessionSeedRatioLimited,
            double sessionSeedRatioLimit,
            bool sessionIdleSeedingLimitEnabled,
            int sessionIdleSeedingLimit,
            bool expected)
        {
            var result = TransmissionSeedLimitEvaluator.HasReachedSeedLimit(
                isStopped,
                isSeeding,
                ratio,
                seedRatioMode,
                seedRatioLimit,
                seedIdleMode,
                seedIdleLimit,
                secondsSeeding,
                sessionSeedRatioLimited,
                sessionSeedRatioLimit,
                sessionIdleSeedingLimitEnabled,
                sessionIdleSeedingLimit);

            Assert.Equal(expected, result);
        }

        [Fact]
        [Trait("Method", "AddAsync")]
        public async Task GetImportItemAsync_WithSpaceInRemoteDirectory()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var download = new Download
            {
                DownloadClientId = CLIENT_CONFIG_ID,
                Metadata = new Dictionary<string, object>
                {
                    ["Uploader"] = "USER2",
                    ["Protocol"] = DownloadProtocol.Torrent
                }
            };
            await _downloadRepository.AddAsync(download);

            var queueItem = new QueueItem
            {
                Id = TransmissionApiMock.ANOTHER_SINGLE_FILE_TORRENT.ToString(),
                Title = "Seconde Fondation",
                Status = "completed",
                ContentPath = FileUtils.GetAbsolutePath("UNKNOWN_YET"),
                DownloadClientId = _client.Id
            };

            var adapter = MockUtils.CreateTransmissionAdapter(_provider);
            var retrievedQeue = await adapter.GetImportItemAsync(_client, download, queueItem);

            Assert.NotNull(retrievedQeue);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows, path cannot ends with a space
                Assert.Equal(FileUtils.GetAbsolutePath("downloads", "complete", "audiobooks", "Isaac Asimov - Le Cycle de Fondation - Tome 3 - Seconde Fondation"), retrievedQeue.ContentPath);
            }
            else
            {
                // On other OS, path should ends with a space
                Assert.Equal(FileUtils.GetAbsolutePath("downloads", "complete", "audiobooks", "Isaac Asimov - Le Cycle de Fondation - Tome 3 - Seconde Fondation "), retrievedQeue.ContentPath);
                Assert.EndsWith(" ", retrievedQeue.ContentPath);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RemoveAsync_PreservesDeleteLocalDataPolicy(bool deleteFiles)
        {
            var adapter = MockUtils.CreateTransmissionAdapter(_provider);

            var result = await adapter.RemoveAsync(_client!, "42", deleteFiles);

            Assert.True(result);
            using var request = _provider.GetRequiredService<TransmissionApiMock>().GetLastJsonContent();
            Assert.NotNull(request);
            Assert.Equal("torrent-remove", request!.RootElement.GetProperty("method").GetString());
            var arguments = request.RootElement.GetProperty("arguments");
            Assert.Equal(deleteFiles, arguments.GetProperty("delete-local-data").GetBoolean());
            Assert.Equal(42, arguments.GetProperty("ids")[0].GetInt32());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(6)]
        public void CompletedTorrentStates_MapToCompleted(int status)
        {
            Assert.Equal(DownloadItemStatus.Completed, TransmissionResponseMapper.MapDownloadItemStatus(status, 100));
            Assert.Equal("completed", TransmissionResponseMapper.MapQueueStatus(status, 100));
        }
    }
}
