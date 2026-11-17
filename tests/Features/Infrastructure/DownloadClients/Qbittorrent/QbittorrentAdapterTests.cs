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
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetItemsAsync_UnknownSeedPolicyCannotAuthorizeMovement(bool knownPolicy)
        {
            using var http = new HttpClient(new DelegatingHandlerMock((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var response = path.EndsWith("/preferences")
                    ? knownPolicy ? """{"max_ratio_enabled":true,"max_ratio":2,"max_seeding_time_enabled":false,"max_seeding_time":30}""" : "{}"
                    : path.EndsWith("/login") ? "Ok."
                    : """[{"hash":"EXACT","name":"Book","state":"stoppedUP","progress":1,"ratio":3,"ratio_limit":-2,"seeding_time_limit":-2}]""";
                return Task.FromResult(MockUtils.GetCannedResponse(response));
            }));
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(http);
            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<QbittorrentAdapter>.Instance;
            var workflow = new QbittorrentItemFetchWorkflow(factory.Object, new QbittorrentAuthSession(logger), logger, "qbittorrent");
            var client = new DownloadClientConfiguration { Host = "localhost", Port = 8080, RemoveCompletedDownloads = "remove_and_delete" };
            var item = Assert.Single(await workflow.GetItemsAsync(client));
            Assert.Equal(knownPolicy, item.CanMoveFiles);
            Assert.Equal(knownPolicy, item.CanBeRemoved);
        }

        // A limit that cannot be read must never be treated as "inherit the global limit", because
        // that can authorize removal the user's per-torrent limit would have forbidden. The torrent
        // stays in the queue but is not removable. The well-formed rows are the control.
        [Theory]
        [InlineData("\"ratio_limit\":2", true)]
        [InlineData("\"seeding_time_limit\":-2", true)]
        [InlineData("\"ratio_limit\":\"2\"", false)]
        [InlineData("\"ratio_limit\":{}", false)]
        [InlineData("\"seeding_time_limit\":\"60\"", false)]
        [InlineData("\"seeding_time_limit\":[]", false)]
        [InlineData("\"seeding_time_limit\":9223372036854775808", false)]
        [InlineData("\"ratio_limit\":null", false)]
        [InlineData("\"seeding_time_limit\":null", false)]
        public async Task GetItemsAsync_UnreadableSeedLimitKeepsTheTorrentButCannotAuthorizeRemoval(string limitField, bool expectedRemovable)
        {
            var item = await GetSingleItemAsync(
                """{"max_ratio_enabled":true,"max_ratio":2,"max_seeding_time_enabled":false,"max_seeding_time":30}""",
                $$"""{"hash":"EXACT","name":"Book","state":"stoppedUP","progress":1,"ratio":3,{{limitField}}}""");
            Assert.Equal(expectedRemovable, item.CanBeRemoved);
        }

        // A seeding time limit is a whole number of minutes, so 60.0 and 6e1 are the same limit as
        // 60, while 60.5 is not a limit qBittorrent can hold and must not be truncated into one.
        // The limit is compared in seconds, so a limit whose conversion would overflow a long
        // (anything above long.MaxValue / 60 = 153722867280912930) cannot be checked at all and
        // must not wrap into a negative bound that every seeding time passes. -1 (no limit, and
        // with no ratio limit either nothing holds the torrent back) and 0 keep their meaning.
        [Theory]
        [InlineData("60", 3600L, true)]
        [InlineData("60", 3599L, false)]
        [InlineData("60.0", 3600L, true)]
        [InlineData("6e1", 3600L, true)]
        [InlineData("60.5", 3600L, false)]
        [InlineData("6.05e1", 3600L, false)]
        [InlineData("0", 0L, true)]
        [InlineData("-1", 0L, true)]
        [InlineData("153722867280912929", long.MaxValue, true)]
        [InlineData("153722867280912930", long.MaxValue, true)]
        [InlineData("153722867280912930", 9223372036854775799L, false)]
        [InlineData("153722867280912931", long.MaxValue, false)]
        [InlineData("9223372036854775807", 600L, false)]
        [InlineData("9223372036854775808", 600L, false)]
        public async Task GetItemsAsync_SeedingTimeLimitMustBeAWholeNumberOfMinutesThatFitsInSeconds(string seedingTimeLimit, long seedingTime, bool expectedRemovable)
        {
            var item = await GetSingleItemAsync(
                """{"max_ratio_enabled":false,"max_ratio":-1,"max_seeding_time_enabled":false,"max_seeding_time":-1}""",
                $$"""{"hash":"EXACT","name":"Book","state":"stoppedUP","progress":1,"ratio":0,"ratio_limit":-1,"seeding_time":{{seedingTime}},"seeding_time_limit":{{seedingTimeLimit}}}""");
            Assert.Equal(expectedRemovable, item.CanBeRemoved);
            Assert.Equal(expectedRemovable, item.CanMoveFiles);
        }

        // The same overflow on the inherited path: a per-torrent limit of -2 defers to the global
        // max_seeding_time, which is converted to seconds the same way. As with a per-torrent
        // limit, a reached ratio does not unblock an inherited limit that cannot be checked; the
        // row with an ordinary inherited limit shows the ratio alone is otherwise enough.
        [Theory]
        [InlineData(60L, 3600L, 0, -1, true)]
        [InlineData(60L, 3599L, 0, -1, false)]
        [InlineData(153722867280912930L, long.MaxValue, 0, -1, true)]
        [InlineData(153722867280912931L, long.MaxValue, 0, -1, false)]
        [InlineData(long.MaxValue, 600L, 0, -1, false)]
        [InlineData(60L, 600L, 3, 1, true)]
        [InlineData(long.MaxValue, 600L, 3, 1, false)]
        public async Task GetItemsAsync_InheritedSeedingTimeLimitThatOverflowsSecondsCannotAuthorizeRemoval(long globalMaxSeedingTime, long seedingTime, double ratio, double ratioLimit, bool expectedRemovable)
        {
            var item = await GetSingleItemAsync(
                $$"""{"max_ratio_enabled":false,"max_ratio":-1,"max_seeding_time_enabled":true,"max_seeding_time":{{globalMaxSeedingTime}}}""",
                $$"""{"hash":"EXACT","name":"Book","state":"stoppedUP","progress":1,"ratio":{{ratio}},"ratio_limit":{{ratioLimit}},"seeding_time":{{seedingTime}},"seeding_time_limit":-2}""");
            Assert.Equal(expectedRemovable, item.CanBeRemoved);
            Assert.Equal(expectedRemovable, item.CanMoveFiles);
        }

        // A reached ratio limit normally authorizes removal on its own. It must not do so while
        // the seeding time limit the user also set cannot be checked; the readable control shows
        // the ratio alone is enough when the time limit is ordinary.
        [Theory]
        [InlineData("120", true)]
        [InlineData("9223372036854775807", false)]
        [InlineData("120.5", false)]
        public async Task GetItemsAsync_ReachedRatioDoesNotOverrideAnUncheckableSeedingTimeLimit(string seedingTimeLimit, bool expectedRemovable)
        {
            var item = await GetSingleItemAsync(
                """{"max_ratio_enabled":false,"max_ratio":-1,"max_seeding_time_enabled":false,"max_seeding_time":-1}""",
                $$"""{"hash":"EXACT","name":"Book","state":"stoppedUP","progress":1,"ratio":3,"ratio_limit":1,"seeding_time":600,"seeding_time_limit":{{seedingTimeLimit}}}""");
            Assert.Equal(expectedRemovable, item.CanBeRemoved);
        }

        // Fetches the item list through the real workflow against one canned preferences response
        // and a single torrent, so a test can vary exactly the fields it is about.
        private static async Task<DownloadClientItem> GetSingleItemAsync(string preferencesJson, string torrentJson)
        {
            using var http = new HttpClient(new DelegatingHandlerMock((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var response = path.EndsWith("/preferences")
                    ? preferencesJson
                    : path.EndsWith("/login") ? "Ok."
                    : $"[{torrentJson}]";
                return Task.FromResult(MockUtils.GetCannedResponse(response));
            }));
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(http);
            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<QbittorrentAdapter>.Instance;
            var workflow = new QbittorrentItemFetchWorkflow(factory.Object, new QbittorrentAuthSession(logger), logger, "qbittorrent");
            var client = new DownloadClientConfiguration { Host = "localhost", Port = 8080, RemoveCompletedDownloads = "remove_and_delete" };
            return Assert.Single(await workflow.GetItemsAsync(client));
        }

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

        // Gateway-level reads reject a save path that is not valid on the host, so the fixtures
        // use host paths (JSON-encoded, as a Windows path carries backslashes).
        private static string SavePathJson(string folder) =>
            JsonSerializer.Serialize(FileUtils.GetAbsolutePath("downloads", folder));

        // A queue response whose middle torrent carries `state` in the given JSON token form.
        // The torrents either side of it are well formed, so anything missing from the result is
        // attributable to that one field.
        //
        // This used to malform `downloaded` instead. #1008's numeric-tolerance fix means no JSON
        // shape in `downloaded` throws any more: a non-Number token (the old quoted-string case)
        // now falls back to the default the same as a missing field would, rather than throwing.
        // `state` is read with `GetString`, which this fix does not touch, so it is still the
        // field that can genuinely make a torrent unreadable.
        private static string QueueWithMalformedMiddleTorrent(string malformedState) => $$"""
        [
            {
                "hash": "aaaa1111", "name": "First", "progress": 0.5, "size": 1000,
                "downloaded": 500, "state": "downloading", "save_path": {{SavePathJson("a")}}
            },
            {
                "hash": "bbbb2222", "name": "Second", "progress": 0.5, "size": 1000,
                "downloaded": 600, "state": {{malformedState}}, "save_path": {{SavePathJson("b")}}
            },
            {
                "hash": "cccc3333", "name": "Third", "progress": 0.5, "size": 1000,
                "downloaded": 700, "state": "downloading", "save_path": {{SavePathJson("c")}}
            }
        ]
        """;

        // qBittorrent documents `state` as a string, so the typed accessor reading it is right
        // about the normal case. It is not resilient about the abnormal one: a non-string value
        // throws `InvalidOperationException` out of the mapper, out of the loop walking the
        // response, and takes every torrent after it along with it, while the poll still reports
        // itself as a healthy live snapshot.
        //
        // These cases used to malform `downloaded` instead, with "600.5" (FormatException),
        // "\"600\"" (InvalidOperationException) and "6e2" (FormatException). #1008's fix makes
        // `downloaded` tolerate all three: a non-integral number truncates, exponent notation
        // truncates, and a quoted string now falls back to the default rather than throwing. None
        // of them is "unreadable" any more, so this malforms `state` instead: "123" and "{}" are
        // both JSON values `GetString` cannot read, and that helper was not touched by the fix.
        [Theory]
        [InlineData("123")]
        [InlineData("{}")]
        public async Task GetQueueAsync_WhenOneTorrentIsUnreadable_DropsOnlyThatTorrent(string malformedState)
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = QueueWithMalformedMiddleTorrent(malformedState);
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client);

            // The torrent AFTER the unreadable one is the whole point. Asserting only that the
            // list is non-empty would pass on the truncating behaviour, because the first torrent
            // is mapped before anything throws.
            Assert.Contains(items, item => item.Id == "aaaa1111");
            Assert.Contains(items, item => item.Id == "cccc3333");
            Assert.DoesNotContain(items, item => item.Id == "bbbb2222");
            Assert.Equal(2, items.Count);
        }

        // The item list is the half of the guard that matters most: completion and import
        // decisions are made from it, so a torrent lost here stops being considered for import
        // rather than merely going missing from a view. Without a case of its own, deleting the
        // guard in QbittorrentItemFetchWorkflow leaves every test in this file green.
        [Theory]
        [InlineData("123")]
        [InlineData("{}")]
        public async Task GetItemsAsync_WhenOneTorrentIsUnreadable_DropsOnlyThatTorrent(string malformedState)
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = QueueWithMalformedMiddleTorrent(malformedState);
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetItemsAsync(_client);

            Assert.Contains(items, item => item.DownloadId == "aaaa1111");
            Assert.Contains(items, item => item.DownloadId == "cccc3333");
            Assert.DoesNotContain(items, item => item.DownloadId == "bbbb2222");
            Assert.Equal(2, items.Count);
        }

        // A single torrent whose given field carries the given raw JSON token, everything else
        // filled in with ordinary, well-formed values. Used below to probe one numeric helper at
        // a time without the three-torrent bookending the drop tests above need. `downloaded` and
        // `progress` are fields under test as well as fixed properties, so they are folded into
        // the substitution itself rather than also appearing as fixed properties, which would
        // otherwise duplicate the JSON key.
        private static string SingleTorrentWithField(string fieldName, string rawToken)
        {
            var downloadedToken = fieldName == "downloaded" ? rawToken : "500";
            var progressToken = fieldName == "progress" ? rawToken : "0.5";
            var extraField = fieldName is "downloaded" or "progress" ? "" : $$""", "{{fieldName}}": {{rawToken}}""";

            return $$"""
            [
                {
                    "hash": "ffff4444", "name": "Single", "progress": {{progressToken}}, "size": 1000,
                    "downloaded": {{downloadedToken}}, "state": "downloading",
                    "save_path": {{SavePathJson("single")}}{{extraField}}
                }
            ]
            """;
        }

        // #1008: `downloaded` is read with `GetInt64`, which called `JsonElement.GetInt64()`
        // directly and threw on any token that was not already a bare integer. The fix tolerates
        // every numeric shape qBittorrent might actually send and only falls back to the default
        // for a token that cannot be read as a number at all.
        //
        // - "600.0" is an integral value written with a fraction: TryGetInt64 fails, TryGetDouble
        //   succeeds (600.0), truncates to 600.
        // - "600.5" is non-integral: TryGetInt64 fails, TryGetDouble succeeds (600.5), truncates.
        // - "6e2" is exponent notation: TryGetInt64 fails even though the value is whole,
        //   TryGetDouble succeeds (600.0), truncates to the same 600.
        // - "1.8446744073709552e+19" is a finite double about 2x long.MaxValue. TryGetDouble
        //   succeeds, but the value is rejected because it cannot be represented as a long, so
        //   this is the case that actually proves the range check exists.
        // - JSON `null` is not a Number at all, so it falls back to the default immediately.
        [Theory]
        [InlineData("600.0", 600L)]
        [InlineData("600.5", 600L)]
        [InlineData("6e2", 600L)]
        [InlineData("1.8446744073709552e+19", 0L)]
        [InlineData("null", 0L)]
        public async Task GetQueueAsync_TheDownloadedFieldToleratesEveryNumericShape(string downloadedToken, long expectedDownloaded)
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = SingleTorrentWithField("downloaded", downloadedToken);
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client);

            // None of these shapes throw any more, so the torrent survives regardless of which
            // one of the five it was given, unlike the drop tests above which exercise genuinely
            // unreadable input.
            var item = Assert.Single(items);
            Assert.Equal(expectedDownloaded, item.Downloaded);
        }

        // Representative coverage for the other numeric helpers touched by the same fix.
        // `dlspeed` goes through `GetDouble`, which has no narrower integer range to reject into:
        // any finite number is accepted as-is, and a non-Number token falls back to the default.
        [Theory]
        [InlineData("500.0", 500.0)]
        [InlineData("6e2", 600.0)]
        [InlineData("{}", 0.0)]
        public async Task GetQueueAsync_TheDlspeedFieldToleratesEveryNumericShape(string dlspeedToken, double expectedDlspeed)
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = SingleTorrentWithField("dlspeed", dlspeedToken);
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client);

            var item = Assert.Single(items);
            Assert.Equal(expectedDlspeed, item.DownloadSpeed);
        }

        // `ratio` goes through `GetNullableDouble`: a non-Number token (here, a quoted string)
        // falls back to null rather than throwing `InvalidOperationException`.
        [Theory]
        [InlineData("1.5", 1.5)]
        [InlineData("\"1.5\"", null)]
        public async Task GetQueueAsync_TheRatioFieldToleratesEveryNumericShape(string ratioToken, double? expectedRatio)
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = SingleTorrentWithField("ratio", ratioToken);
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client);

            var item = Assert.Single(items);
            Assert.Equal(expectedRatio, item.Ratio);
        }

        // `eta` goes through `GetNullableInt32`: the same truncate-or-reject behavior as
        // `downloaded`, just on the 32-bit nullable path, plus the JSON `null` literal case.
        [Theory]
        [InlineData("120.5", 120)]
        [InlineData("6e2", 600)]
        [InlineData("null", null)]
        public async Task GetQueueAsync_TheEtaFieldToleratesEveryNumericShape(string etaToken, int? expectedEta)
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = SingleTorrentWithField("eta", etaToken);
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client);

            var item = Assert.Single(items);
            Assert.Equal(expectedEta, item.Eta);
        }

        // `added_on` only orders and labels the queue view, so a value that is not a usable Unix time must
        // not cost the torrent its place in the snapshot. Anything negative, beyond the last
        // second DateTime can hold (253402300799), or not a number at all maps to the Unix epoch,
        // the same value a missing `added_on` has always produced. The quoted form is documented
        // as an integer by qBittorrent and is not parsed.
        [Theory]
        [InlineData("1600000000", 1600000000L)]
        [InlineData("1.6e9", 1600000000L)]
        [InlineData("253402300799", 253402300799L)]
        [InlineData("253402300800", 0L)]
        [InlineData("1e12", 0L)]
        [InlineData("99999999999999", 0L)]
        [InlineData("-1", 0L)]
        [InlineData("-62135596801", 0L)]
        [InlineData("\"1600000000\"", 0L)]
        [InlineData("\"NaN\"", 0L)]
        [InlineData("null", 0L)]
        public async Task GetQueueAsync_AnUnusableAddedOnKeepsTheTorrentAndFallsBackToTheEpoch(string addedOnToken, long expectedUnixSeconds)
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = SingleTorrentWithField("added_on", addedOnToken);
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client);

            var item = Assert.Single(items);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expectedUnixSeconds).DateTime, item.AddedAt);
        }

        // `progress` has no JSON spelling for NaN or infinity, so a quoted "NaN" or a literal too
        // large for a double is not a number this mapper reads. It falls back to 0 instead of
        // reaching `Progress`, where a non-finite value would break serializing the queue.
        [Theory]
        [InlineData("0.25", 25.0)]
        [InlineData("\"NaN\"", 0.0)]
        [InlineData("1e400", 0.0)]
        public async Task GetQueueAsync_AProgressThatIsNotAFiniteNumberFallsBackToZero(string progressToken, double expectedProgress)
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = SingleTorrentWithField("progress", progressToken);
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetQueueAsync(_client);

            var item = Assert.Single(items);
            Assert.Equal(expectedProgress, item.Progress);
            _ = JsonSerializer.Serialize(item);
        }

        // `seeding_time` goes through `GetNullableInt64` and is only consumed internally by the
        // seed-limit evaluator, with no field of its own on the mapped item. Unlike the fields
        // above, this case only confirms the mapping survives at all for a malformed value; it
        // doesn't assert the resulting seed-limit decision, since that depends on several other
        // fields too.
        [Theory]
        [InlineData("7200.5")]
        [InlineData("{}")]
        public async Task GetItemsAsync_TheSeedingTimeFieldNeverThrows(string seedingTimeToken)
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = SingleTorrentWithField("seeding_time", seedingTimeToken);
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            var items = await adapter.GetItemsAsync(_client);

            Assert.Single(items);
        }

        // The counterpart control for the guard above. The per-torrent files request lives inside
        // the guarded block, so a client that stops answering after the torrent list arrives
        // throws once per remaining torrent. If the guard swallowed those, a monitor poll would
        // return a short queue and report success, and the monitor would neither back off nor say
        // anything, which is a worse outcome than the truncation the guard was added to fix.
        [Fact]
        public async Task GetQueueAsync_WithIds_WhenTheClientStopsAnsweringMidPoll_StillFailsThePoll()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = QueueWithMalformedMiddleTorrent("\"downloading\"");
            apiMock.FilesRequestFailsAtTransport = true;
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            await Assert.ThrowsAsync<DownloadClientAdapterPollingException>(
                () => adapter.GetQueueAsync(_client, ["aaaa1111", "bbbb2222", "cccc3333"]));
        }

        // The same failure arriving through the circuit breaker instead of the transport. Once the
        // breaker opens after the torrent list has arrived, every files request that follows is
        // rejected with BrokenCircuitException, which is not an HttpRequestException. Swallowed as
        // an unreadable torrent, it turns a failed poll into a short successful one, and the
        // monitor resets its retry backoff instead of growing it.
        [Fact]
        public async Task GetQueueAsync_WithIds_WhenTheBreakerOpensMidPoll_StillFailsThePoll()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = QueueWithMalformedMiddleTorrent("\"downloading\"");
            apiMock.FilesRequestsBeforeBreakerOpens = 1;
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            await Assert.ThrowsAsync<DownloadClientAdapterPollingException>(
                () => adapter.GetQueueAsync(_client, ["aaaa1111", "bbbb2222", "cccc3333"]));
        }

        // A full snapshot has no polling exception to raise, so the breaker failure lands in the
        // outer handler with the first torrent already read. That row is still worth showing, but
        // the torrents after it were never looked at, so the snapshot must not drive cleanup.
        [Fact]
        public async Task OrphanCleanup_WhenTheBreakerOpensMidPoll_KeepsTheUnreadTorrentsRecords()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = QueueWithMalformedMiddleTorrent("\"downloading\"");
            apiMock.FilesRequestsBeforeBreakerOpens = 1;
            var tracked = await TrackDownloadsAsync("aaaa1111", "bbbb2222", "cccc3333");

            await RunOrphanCleanupAsync(tracked);

            Assert.NotNull(await _downloadRepository.GetByIdAsync("tracked-bbbb2222"));
            Assert.NotNull(await _downloadRepository.GetByIdAsync("tracked-cccc3333"));
        }

        // The skip is a recurring condition, not an event. A torrent whose fields the mapper
        // cannot read stays unreadable, so at the monitor's default cadence one such torrent
        // writes a line on every poll for as long as it sits in the client. Warning would be
        // asking the operator to act on something they cannot act on, so these two tests pin
        // the level the way TransmissionQueueFetchWorkflow already logs the same condition.
        [Fact]
        public async Task GetQueueAsync_WhenOneTorrentIsUnreadable_LogsTheSkipAtDebug()
        {
            var logs = new RecordingLoggerProvider();
            Init(builder => builder
                .WithSingleton<ILoggerProvider>(logs)
                .WithMocks(RecordingLoggerProvider.CaptureEveryLevel));
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = QueueWithMalformedMiddleTorrent("123");
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            await adapter.GetQueueAsync(_client);

            var skips = logs.EntriesContaining("Skipping unreadable qBittorrent torrent");
            Assert.NotEmpty(skips);
            Assert.All(skips, entry => Assert.Equal(LogLevel.Debug, entry.Level));
        }

        [Fact]
        public async Task GetItemsAsync_WhenOneTorrentIsUnreadable_LogsTheSkipAtDebug()
        {
            var logs = new RecordingLoggerProvider();
            Init(builder => builder
                .WithSingleton<ILoggerProvider>(logs)
                .WithMocks(RecordingLoggerProvider.CaptureEveryLevel));
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = QueueWithMalformedMiddleTorrent("123");
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            await adapter.GetItemsAsync(_client);

            var skips = logs.EntriesContaining("Skipping unreadable qBittorrent torrent");
            Assert.NotEmpty(skips);
            Assert.All(skips, entry => Assert.Equal(LogLevel.Debug, entry.Level));
        }

        // The hash is read out of the client's own JSON and then interpolated into the files
        // query. The loop above now checks the token form of that value, so leaving the same
        // value undefended one line later is the odd place to stop. With no escaping, a hash
        // carrying an ampersand truncates at the delimiter and the client is asked about a
        // torrent that does not exist, which returns an empty file list and no error.
        [Fact]
        public async Task GetQueueAsync_EscapesTheTorrentHashInTheFilesRequest()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = """
            [
                {
                    "hash": "aaaa1111&bbbb=2222", "name": "First", "progress": 0.5, "size": 1000,
                    "downloaded": 500, "state": "downloading", "save_path": "/downloads/a"
                }
            ]
            """;
            apiMock.ResetRequestHistory();
            var gateway = (DownloadClientGateway)_provider.GetRequiredService<IDownloadClientGateway>();
            var adapter = (QbittorrentAdapter)gateway.ResolveAdapter(_client);

            await adapter.GetQueueAsync(_client);

            var filesRequest = Assert.Single(apiMock.RequestHistory,
                request => request.RequestUri.AbsolutePath.EndsWith("/api/v2/torrents/files", StringComparison.Ordinal));
            var query = HttpUtility.ParseQueryString(filesRequest.RequestUri.Query);
            Assert.Equal("aaaa1111&bbbb=2222", query["hash"]);
        }

        // Orphan cleanup removes a tracked download once its hash has been missing from a live
        // queue snapshot for longer than the grace period. A torrent skipped because it could not
        // be read is missing from the rows but not from the client, so a snapshot with a skip in
        // it cannot say what is gone.
        [Fact]
        public async Task OrphanCleanup_WhenOneTorrentIsUnreadable_KeepsItsTrackingRecord()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = QueueWithMalformedMiddleTorrent("123");
            var tracked = await TrackDownloadsAsync("aaaa1111", "bbbb2222", "cccc3333");

            await RunOrphanCleanupAsync(tracked);

            Assert.NotNull(await _downloadRepository.GetByIdAsync("tracked-bbbb2222"));
        }

        // The control for the test above. Without it, a record that survives could mean the
        // apparatus never removes anything. With every torrent readable, the same cleanup still
        // removes a record whose torrent is genuinely gone and keeps the ones still present.
        [Fact]
        public async Task OrphanCleanup_WhenEveryTorrentIsReadable_StillRemovesAGenuineOrphan()
        {
            var apiMock = _provider.GetRequiredService<QbittorrentApiMock>();
            apiMock.InfoResponseOverride = QueueWithMalformedMiddleTorrent("\"downloading\"");
            var tracked = await TrackDownloadsAsync("aaaa1111", "bbbb2222", "cccc3333", "dddd4444");

            await RunOrphanCleanupAsync(tracked);

            Assert.Null(await _downloadRepository.GetByIdAsync("tracked-dddd4444"));
            Assert.NotNull(await _downloadRepository.GetByIdAsync("tracked-bbbb2222"));
        }

        // Tracked downloads old enough that orphan cleanup's grace period no longer protects them.
        private async Task<List<Download>> TrackDownloadsAsync(params string[] hashes)
        {
            var tracked = new List<Download>();
            foreach (var hash in hashes)
            {
                var download = new DownloadBuilder()
                    .WithId($"tracked-{hash}")
                    .WithTitle(hash)
                    .WithStatus(DownloadStatus.Downloading)
                    .WithStartDate(DateTime.UtcNow.AddMinutes(-10))
                    .WithExternalId(hash)
                    .Build();
                download.DownloadClientId = _client.Id;
                tracked.Add(await _downloadRepository.AddAsync(download));
            }

            return tracked;
        }

        // The orphan half of the monitor cycle: a full snapshot through the same poller the
        // monitor uses, handed to the same cleanup service.
        private async Task RunOrphanCleanupAsync(List<Download> tracked)
        {
            var poller = _provider.GetRequiredService<DownloadClientQueuePoller>();
            var snapshot = Assert.Single(await poller.FetchAsync(
                [_client],
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMinutes(3),
                maxParallelClientPolls: 1));

            await _provider.GetRequiredService<DownloadOrphanCleanupService>()
                .RemoveOrphansAsync(_client, snapshot, snapshot.QueueItems, tracked);
        }

        private sealed record RecordedLogEntry(LogLevel Level, string Message);

        // Enough of a logger to answer "at what level was this written", which nothing else in
        // the suite needed until now. Registered as the only ILoggerProvider, so it sees every
        // category including the adapter's.
        private sealed class RecordingLoggerProvider : ILoggerProvider
        {
            private readonly object _gate = new();
            private readonly List<RecordedLogEntry> _entries = [];

            // AddLogging() floors the factory at Information, so without this the Debug lines
            // these tests exist to see would never reach a provider at all.
            public static ServiceDescriptor CaptureEveryLevel { get; } =
                ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IConfigureOptions<LoggerFilterOptions>>(
                    new Microsoft.Extensions.Options.ConfigureOptions<LoggerFilterOptions>(
                        options => options.MinLevel = LogLevel.Trace));

            public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

            public IReadOnlyList<RecordedLogEntry> EntriesContaining(string fragment)
            {
                lock (_gate)
                {
                    return [.. _entries.Where(entry => entry.Message.Contains(fragment, StringComparison.Ordinal))];
                }
            }

            public void Dispose()
            {
            }

            private void Record(LogLevel level, string message)
            {
                lock (_gate)
                {
                    _entries.Add(new RecordedLogEntry(level, message));
                }
            }

            private sealed class RecordingLogger(RecordingLoggerProvider owner) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(
                    LogLevel logLevel,
                    EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter)
                {
                    ArgumentNullException.ThrowIfNull(formatter);
                    owner.Record(logLevel, formatter(state, exception));
                }
            }
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
        [InlineData("pausedUP")] // qBittorrent < 5.0 / rdt-client (#1047)
        public void CompletedTorrentStates_MapToCompleted(string state)
        {
            Assert.Equal(DownloadItemStatus.Completed, QbittorrentResponseMapper.MapDownloadItemStatus(state, 100));
            Assert.Equal("completed", QbittorrentResponseMapper.MapQueueStatus(state, 100));
        }

        [Theory]
        [InlineData("pausedDL")]
        [InlineData("pausedUP")]
        public void LegacyPausedStates_MapToPaused_WhenIncomplete(string state)
        {
            // Before #1047 these pre-5.0 states fell through to Warning/unknown, so an rdt-client
            // or old-qBittorrent download never progressed to completion or import.
            Assert.Equal(DownloadItemStatus.Paused, QbittorrentResponseMapper.MapDownloadItemStatus(state, 50));
            Assert.Equal("paused", QbittorrentResponseMapper.MapQueueStatus(state, 50));
        }
    }
}
