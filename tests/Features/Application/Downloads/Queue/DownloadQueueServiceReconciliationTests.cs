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
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Downloads.Queue
{
    [Trait("Area", "QueueReconciliation")]
    public class DownloadQueueServiceReconciliationTests : IDisposable
    {
        private readonly List<IDisposable> _disposables = new();

        private DownloadQueueService CreateService(
            IConfigurationService configurationService,
            IDownloadRepository downloadRepository,
            IDownloadProcessingJobRepository processingJobRepository,
            IDownloadClientGateway clientGateway,
            IAppMetricsService metrics,
            IMemoryCache? memoryCache = null,
            TimeSpan? clientQueueTimeout = null,
            TimeSpan? staleSnapshotMaxAge = null)
        {
            var resolvedMemoryCache = memoryCache ?? Track(new MemoryCache(new MemoryCacheOptions()));
            var pathMapping = new Mock<IRemotePathMappingService>();
            var httpFactory = new Mock<IHttpClientFactory>();
            var httpClient = Track(new HttpClient());
            httpFactory.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var scopeProvider = Track(new ServiceCollection().BuildServiceProvider());
            var scopeFactory = scopeProvider.GetRequiredService<IServiceScopeFactory>();
            var candidateLoader = new DownloadQueueCandidateLoader(
                downloadRepository,
                processingJobRepository,
                NullLogger<DownloadQueueCandidateLoader>.Instance);
            var clientQueuePoller = new DownloadClientQueuePoller(
                resolvedMemoryCache,
                clientGateway,
                metrics,
                NullLogger<DownloadClientQueuePoller>.Instance);

            var service = new DownloadQueueService(
                resolvedMemoryCache,
                configurationService,
                downloadRepository,
                candidateLoader,
                clientQueuePoller,
                metrics,
                NullLogger<DownloadQueueService>.Instance);

            service._clientQueueTimeout = (TimeSpan)(clientQueueTimeout == null ? service._clientQueueTimeout : clientQueueTimeout);
            service._staleSnapshotMaxAge = (TimeSpan)(staleSnapshotMaxAge == null ? service._staleSnapshotMaxAge : staleSnapshotMaxAge);

            return service;
        }

        private T Track<T>(T disposable) where T : IDisposable
        {
            _disposables.Add(disposable);
            return disposable;
        }

        public void Dispose()
        {
            for (var i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }
        }

        private static void SetupQueueRepository(Mock<IDownloadRepository> downloadRepoMock, List<Download> downloads)
        {
            downloadRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(downloads);
            downloadRepoMock
                .Setup(r => r.GetQueueDisplayCandidatesAsync())
                .ReturnsAsync(downloads
                    .Where(IsQueueDisplayCandidate)
                    .ToList());
            downloadRepoMock
                .Setup(r => r.GetQueueMatchingCandidatesAsync())
                .ReturnsAsync(downloads
                    .Where(d => d.DownloadClientId != "DDL" && d.Status != DownloadStatus.Failed)
                    .ToList());
            downloadRepoMock
                .Setup(r => r.GetKnownClientItemIdsAsync())
                .ReturnsAsync(downloads
                    .SelectMany(d => GetKnownClientItemIds(d.Metadata))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());
        }

        private static IEnumerable<string> GetKnownClientItemIds(Dictionary<string, object>? metadata)
        {
            if (metadata == null)
            {
                yield break;
            }

            if (metadata.TryGetValue("ClientDownloadId", out var clientDownloadId) &&
                !string.IsNullOrWhiteSpace(clientDownloadId?.ToString()))
            {
                yield return clientDownloadId.ToString()!;
            }

            if (metadata.TryGetValue("TorrentHash", out var torrentHash) &&
                !string.IsNullOrWhiteSpace(torrentHash?.ToString()))
            {
                yield return torrentHash.ToString()!;
            }
        }

        [Fact]
        [Trait("Scenario", "QueueRebindPrefersIdBeforeTitle")]
        public async Task GetQueueAsync_RebindsByIdBeforeTitle_WhenMultipleMatchesExist()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "qbit",
                Type = "qbittorrent",
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { client });
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { ShowCompletedExternalDownloads = false });

            var dbTitleMatch = new Download
            {
                Id = "db-title-match",
                DownloadClientId = "qb-1",
                Title = "Dune - Frank Herbert [M4B]",
                Status = DownloadStatus.Downloading,
                StartedAt = DateTime.UtcNow.AddMinutes(-10)
            };

            var dbIdMatch = new Download
            {
                Id = "db-id-match",
                DownloadClientId = "qb-1",
                Title = "Different Title",
                Status = DownloadStatus.Downloading,
                StartedAt = DateTime.UtcNow.AddMinutes(-5)
            };

            var downloadRepoMock = new Mock<IDownloadRepository>();
            SetupQueueRepository(downloadRepoMock, new List<Download> { dbTitleMatch, dbIdMatch });

            var processingJobRepoMock = new Mock<IDownloadProcessingJobRepository>();
            processingJobRepoMock.Setup(r => r.GetPendingDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
            processingJobRepoMock.Setup(r => r.GetAllJobDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock.Setup(g => g.GetQueueAsync(client, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>
                {
                    new QueueItem
                    {
                        Id = "db-id-match",
                        Title = "Dune - Frank Herbert [M4B]",
                        Status = "downloading",
                        AddedAt = DateTime.UtcNow
                    }
                });

            var metricsMock = new Mock<IAppMetricsService>();

            var service = CreateService(
                configMock.Object,
                downloadRepoMock.Object,
                processingJobRepoMock.Object,
                gatewayMock.Object,
                metricsMock.Object);

            var result = await service.GetQueueAsync();

            Assert.Single(result);
            Assert.Equal("db-id-match", result[0].Id);
        }

        [Fact]
        [Trait("Scenario", "QueueUsesTargetedRepositoryQueries")]
        public async Task GetQueueAsync_UsesTargetedRepositoryQueries_InsteadOfGetAllAsync()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "qbit",
                Type = "qbittorrent",
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { client });
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { ShowCompletedExternalDownloads = false });

            var downloadRepoMock = new Mock<IDownloadRepository>();
            downloadRepoMock.Setup(r => r.GetAllAsync()).ThrowsAsync(new InvalidOperationException("GetAllAsync should not be used by queue reconciliation"));
            downloadRepoMock.Setup(r => r.GetQueueDisplayCandidatesAsync()).ReturnsAsync(new List<Download>());
            downloadRepoMock.Setup(r => r.GetQueueMatchingCandidatesAsync()).ReturnsAsync(new List<Download>());
            downloadRepoMock.Setup(r => r.GetKnownClientItemIdsAsync()).ReturnsAsync(new List<string>());

            var processingJobRepoMock = new Mock<IDownloadProcessingJobRepository>();
            processingJobRepoMock.Setup(r => r.GetPendingDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
            processingJobRepoMock.Setup(r => r.GetAllJobDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock.Setup(g => g.GetQueueAsync(client, It.IsAny<CancellationToken>())).ReturnsAsync(new List<QueueItem>());

            var metricsMock = new Mock<IAppMetricsService>();

            var service = CreateService(
                configMock.Object,
                downloadRepoMock.Object,
                processingJobRepoMock.Object,
                gatewayMock.Object,
                metricsMock.Object);

            var result = await service.GetQueueAsync();

            Assert.Empty(result);
            downloadRepoMock.Verify(r => r.GetQueueDisplayCandidatesAsync(), Times.Once);
            downloadRepoMock.Verify(r => r.GetQueueMatchingCandidatesAsync(), Times.Once);
            downloadRepoMock.Verify(r => r.GetKnownClientItemIdsAsync(), Times.Once);
            downloadRepoMock.Verify(r => r.GetAllAsync(), Times.Never);
        }

        [Fact]
        [Trait("Scenario", "MissingSnapshotRetainsTrackedDownload")]
        public async Task GetQueueAsync_MissingQueueSnapshot_RetainsTrackedRecord_WithoutOrphanMetric()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "tr-1",
                Name = "transmission",
                Type = "transmission",
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { client });
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { ShowCompletedExternalDownloads = false });

            var tracked = new Download
            {
                Id = "tracked-1",
                DownloadClientId = "tr-1",
                Title = "Queue Missing Candidate",
                Status = DownloadStatus.Downloading,
                StartedAt = DateTime.UtcNow.AddMinutes(-20)
            };

            var downloadRepoMock = new Mock<IDownloadRepository>();
            SetupQueueRepository(downloadRepoMock, new List<Download> { tracked });

            var processingJobRepoMock = new Mock<IDownloadProcessingJobRepository>();
            processingJobRepoMock.Setup(r => r.GetPendingDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
            processingJobRepoMock.Setup(r => r.GetAllJobDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock.Setup(g => g.GetQueueAsync(client, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>());

            var metricsMock = new Mock<IAppMetricsService>();

            var service = CreateService(
                configMock.Object,
                downloadRepoMock.Object,
                processingJobRepoMock.Object,
                gatewayMock.Object,
                metricsMock.Object);

            var result = await service.GetQueueAsync();

            Assert.Empty(result);
            metricsMock.Verify(m => m.Increment("download.purge.skipped.tracked_orphan_retained", It.IsAny<int>()), Times.Never);
        }

        [Fact]
        [Trait("Scenario", "ClientTimeoutUsesCachedSnapshot")]
        public async Task GetQueueAsync_ClientTimeout_UsesCachedSnapshotFallback()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { client });
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { ShowCompletedExternalDownloads = false });

            var trackedDownload = new Download
            {
                Id = "tracked-1",
                DownloadClientId = "qb-1",
                Title = "Dune - Frank Herbert [M4B]",
                Status = DownloadStatus.Downloading,
                StartedAt = DateTime.UtcNow.AddMinutes(-20),
                Metadata = new Dictionary<string, object>
                {
                    ["ClientDownloadId"] = "HASH1",
                    ["TorrentHash"] = "HASH1"
                }
            };

            var downloadRepoMock = new Mock<IDownloadRepository>();
            SetupQueueRepository(downloadRepoMock, new List<Download> { trackedDownload });
            downloadRepoMock
                .Setup(r => r.UpdateMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.CompletedTask);

            var processingJobRepoMock = new Mock<IDownloadProcessingJobRepository>();
            processingJobRepoMock.Setup(r => r.GetPendingDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
            processingJobRepoMock.Setup(r => r.GetAllJobDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock.SetupSequence(g => g.GetQueueAsync(client, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>
                {
                    new QueueItem
                    {
                        Id = "HASH1",
                        Title = "Dune - Frank Herbert [M4B]",
                        Status = "downloading",
                        AddedAt = DateTime.UtcNow
                    }
                })
                .Returns(Task.Delay(TimeSpan.FromSeconds(5))
                    .ContinueWith(_ => new List<QueueItem>(), TaskScheduler.Default));

            var metricsMock = new Mock<IAppMetricsService>();

            var service = CreateService(
                configMock.Object,
                downloadRepoMock.Object,
                processingJobRepoMock.Object,
                gatewayMock.Object,
                metricsMock.Object,
                clientQueueTimeout: TimeSpan.FromMilliseconds(75),
                staleSnapshotMaxAge: TimeSpan.FromMinutes(1));

            var first = await service.GetQueueAsync();
            Assert.Single(first);
            Assert.Equal("tracked-1", first[0].Id);
            Assert.False(first[0].IsStaleSnapshot);
            Assert.Equal("live", first[0].SnapshotState);

            var startedAt = DateTime.UtcNow;
            var second = await service.GetQueueAsync();
            var elapsed = DateTime.UtcNow - startedAt;

            Assert.Single(second);
            Assert.Equal("tracked-1", second[0].Id);
            Assert.True(second[0].IsStaleSnapshot);
            Assert.Equal("cached", second[0].SnapshotState);
            Assert.Equal("timeout", second[0].SnapshotFailureReason);
            Assert.NotNull(second[0].SnapshotAgeSeconds);
            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Expected cached fallback to return quickly, but took {elapsed.TotalMilliseconds:F0}ms");
            metricsMock.Verify(m => m.Increment("download.queue.client.poll.timeout", It.Is<double>(v => Math.Abs(v - 1.0d) < 1e-9)), Times.Once);
            metricsMock.Verify(m => m.Increment("download.queue.client.snapshot.fallback", It.Is<double>(v => Math.Abs(v - 1.0d) < 1e-9)), Times.Once);
        }

        [Fact]
        [Trait("Scenario", "UnavailableClientSurfacedInSnapshot")]
        public async Task GetQueueSnapshotAsync_ClientTimeoutWithoutCache_ReturnsUnavailableClientStatus()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { client });
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { ShowCompletedExternalDownloads = false });

            var downloadRepoMock = new Mock<IDownloadRepository>();
            SetupQueueRepository(downloadRepoMock, new List<Download>());

            var processingJobRepoMock = new Mock<IDownloadProcessingJobRepository>();
            processingJobRepoMock.Setup(r => r.GetPendingDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
            processingJobRepoMock.Setup(r => r.GetAllJobDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock.Setup(g => g.GetQueueAsync(client, It.IsAny<CancellationToken>()))
                .Returns(Task.Delay(TimeSpan.FromSeconds(5))
                    .ContinueWith(_ => new List<QueueItem>(), TaskScheduler.Default));

            var metricsMock = new Mock<IAppMetricsService>();

            var service = CreateService(
                configMock.Object,
                downloadRepoMock.Object,
                processingJobRepoMock.Object,
                gatewayMock.Object,
                metricsMock.Object,
                clientQueueTimeout: TimeSpan.FromMilliseconds(75),
                staleSnapshotMaxAge: TimeSpan.FromMilliseconds(75));

            var snapshot = await service.GetQueueSnapshotAsync();

            Assert.Empty(snapshot.Items);
            Assert.Single(snapshot.Clients);
            Assert.True(snapshot.HasUnavailableClients);
            Assert.False(snapshot.HasStaleData);
            Assert.Equal("qb-1", snapshot.Clients[0].ClientId);
            Assert.True(snapshot.Clients[0].IsUnavailable);
            Assert.False(snapshot.Clients[0].IsStaleSnapshot);
            Assert.Equal("unavailable", snapshot.Clients[0].SnapshotState);
            Assert.Equal("timeout", snapshot.Clients[0].SnapshotFailureReason);
        }

        [Fact]
        [Trait("Scenario", "DisabledClientsAreNotPolled")]
        public async Task GetQueueAsync_DoesNotPollDisabledClients()
        {
            var enabledClient = new DownloadClientConfiguration
            {
                Id = "enabled-1",
                Name = "Enabled",
                Type = "transmission",
                IsEnabled = true
            };

            var disabledClient = new DownloadClientConfiguration
            {
                Id = "disabled-1",
                Name = "Disabled",
                Type = "qbittorrent",
                IsEnabled = false
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { enabledClient, disabledClient });
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { ShowCompletedExternalDownloads = false });

            var downloadRepoMock = new Mock<IDownloadRepository>();
            SetupQueueRepository(downloadRepoMock, new List<Download>());

            var processingJobRepoMock = new Mock<IDownloadProcessingJobRepository>();
            processingJobRepoMock.Setup(r => r.GetPendingDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
            processingJobRepoMock.Setup(r => r.GetAllJobDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock.Setup(g => g.GetQueueAsync(enabledClient, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>());

            var metricsMock = new Mock<IAppMetricsService>();

            var service = CreateService(
                configMock.Object,
                downloadRepoMock.Object,
                processingJobRepoMock.Object,
                gatewayMock.Object,
                metricsMock.Object);

            await service.GetQueueAsync();

            gatewayMock.Verify(g => g.GetQueueAsync(enabledClient, It.IsAny<CancellationToken>()), Times.Once);
            gatewayMock.Verify(g => g.GetQueueAsync(disabledClient, It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Scenario", "JsonElementMetadataSuppressesTrackedCompletedExternalItem")]
        public async Task GetQueueAsync_KnownClientIdStoredAsJsonElement_DoesNotSurfaceUnmatchedCompletedItem()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "tr-1",
                Name = "Transmission",
                Type = "transmission",
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { client });
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { ShowCompletedExternalDownloads = true });

            using var clientIdDoc = JsonDocument.Parse("\"HASH1\"");
            var trackedDownload = new Download
            {
                Id = "tracked-1",
                DownloadClientId = "old-client",
                Title = "Tracked elsewhere",
                Status = DownloadStatus.Failed,
                Metadata = new Dictionary<string, object>
                {
                    ["ClientDownloadId"] = clientIdDoc.RootElement.Clone()
                }
            };

            var downloadRepoMock = new Mock<IDownloadRepository>();
            SetupQueueRepository(downloadRepoMock, new List<Download> { trackedDownload });

            var processingJobRepoMock = new Mock<IDownloadProcessingJobRepository>();
            processingJobRepoMock.Setup(r => r.GetPendingDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
            processingJobRepoMock.Setup(r => r.GetAllJobDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock.Setup(g => g.GetQueueAsync(client, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>
                {
                    new QueueItem
                    {
                        Id = "HASH1",
                        Title = "Tracked elsewhere",
                        Status = "completed",
                        AddedAt = DateTime.UtcNow
                    }
                });

            var metricsMock = new Mock<IAppMetricsService>();

            var service = CreateService(
                configMock.Object,
                downloadRepoMock.Object,
                processingJobRepoMock.Object,
                gatewayMock.Object,
                metricsMock.Object);

            var result = await service.GetQueueAsync();

            Assert.Empty(result);
        }

        [Fact]
        [Trait("Scenario", "CompletedPendingImportMapsBackToTrackedId")]
        public async Task GetQueueAsync_MapsCompletedPendingImport_ExternalItemToTrackedDownloadId()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { client });
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { ShowCompletedExternalDownloads = false });

            var trackedDownload = new Download
            {
                Id = "tracked-1",
                DownloadClientId = "qb-1",
                Title = "Dune - Frank Herbert [M4B]",
                Status = DownloadStatus.Completed,
                FinalPath = string.Empty,
                StartedAt = DateTime.UtcNow.AddMinutes(-20),
                Metadata = new Dictionary<string, object>
                {
                    ["TorrentHash"] = "061850ead3eb6f1c5c6d8420211b4bbf2d4ee3e2"
                }
            };

            var downloadRepoMock = new Mock<IDownloadRepository>();
            SetupQueueRepository(downloadRepoMock, new List<Download> { trackedDownload });
            downloadRepoMock
                .Setup(r => r.UpdateMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>()))
                .Returns(Task.CompletedTask);

            var processingJobRepoMock = new Mock<IDownloadProcessingJobRepository>();
            processingJobRepoMock.Setup(r => r.GetPendingDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
            processingJobRepoMock.Setup(r => r.GetAllJobDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock.Setup(g => g.GetQueueAsync(client, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>
                {
                    new QueueItem
                    {
                        Id = "061850ead3eb6f1c5c6d8420211b4bbf2d4ee3e2",
                        Title = "Dune - Frank Herbert [M4B]",
                        Status = "completed",
                        Progress = 100,
                        DownloadClient = "local qbit",
                        DownloadClientId = "qb-1",
                        DownloadClientType = "qbittorrent",
                        AddedAt = DateTime.UtcNow.AddHours(-2)
                    }
                });

            var metricsMock = new Mock<IAppMetricsService>();

            var service = CreateService(
                configMock.Object,
                downloadRepoMock.Object,
                processingJobRepoMock.Object,
                gatewayMock.Object,
                metricsMock.Object);

            var result = await service.GetQueueAsync();

            Assert.Single(result);
            Assert.Equal("tracked-1", result[0].Id);
            Assert.Equal("completed", result[0].Status, ignoreCase: true, ignoreLineEndingDifferences: false, ignoreWhiteSpaceDifferences: false, ignoreAllWhiteSpace: false);
            downloadRepoMock.Verify(
                r => r.UpdateMetadataAsync("tracked-1", "ClientDownloadId", "061850ead3eb6f1c5c6d8420211b4bbf2d4ee3e2"),
                Times.Once);
        }

        [Fact]
        [Trait("Scenario", "ShortTitleArtistAwareReconciliation")]
        public async Task GetQueueAsync_MatchesShortTitleUsingArtistAwareFallback_AndAvoidsDuplicateCompletedEntry()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "local qbit",
                Type = "qbittorrent",
                IsEnabled = true
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetDownloadClientConfigurationsAsync())
                .ReturnsAsync(new List<DownloadClientConfiguration> { client });
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { ShowCompletedExternalDownloads = true });

            var trackedDownload = new Download
            {
                Id = "tracked-artemis",
                DownloadClientId = "qb-1",
                Title = "Artemis",
                Artist = "Andy Weir",
                Status = DownloadStatus.Completed,
                FinalPath = @"C:\library\Andy Weir\Artemis",
                StartedAt = DateTime.UtcNow.AddMinutes(-20)
            };

            var persistedMetadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var downloadRepoMock = new Mock<IDownloadRepository>();
            SetupQueueRepository(downloadRepoMock, new List<Download> { trackedDownload });
            downloadRepoMock
                .Setup(r => r.UpdateMetadataAsync("tracked-artemis", It.IsAny<string>(), It.IsAny<object?>()))
                .Callback<string, string, object?>((_, key, value) => persistedMetadata[key] = value)
                .Returns(Task.CompletedTask);

            var processingJobRepoMock = new Mock<IDownloadProcessingJobRepository>();
            processingJobRepoMock.Setup(r => r.GetPendingDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());
            processingJobRepoMock.Setup(r => r.GetAllJobDownloadIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(new List<string>());

            var gatewayMock = new Mock<IDownloadClientGateway>();
            gatewayMock.Setup(g => g.GetQueueAsync(client, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QueueItem>
                {
                    new QueueItem
                    {
                        Id = "HASH-ARTEMIS",
                        Title = "Andy Weir - Artemis - 2017 - 125 kbps.m4b",
                        Status = "downloading",
                        Progress = 100,
                        DownloadClient = "local qbit",
                        DownloadClientId = "qb-1",
                        DownloadClientType = "qbittorrent",
                        AddedAt = DateTime.UtcNow.AddHours(-2)
                    }
                });

            var metricsMock = new Mock<IAppMetricsService>();

            var service = CreateService(
                configMock.Object,
                downloadRepoMock.Object,
                processingJobRepoMock.Object,
                gatewayMock.Object,
                metricsMock.Object);

            var result = await service.GetQueueAsync();

            Assert.Single(result);
            Assert.Equal("tracked-artemis", result[0].Id);
            Assert.Equal("Artemis", result[0].Title);
            Assert.Equal("HASH-ARTEMIS", trackedDownload.Metadata?["ClientDownloadId"]?.ToString());
            Assert.Equal("HASH-ARTEMIS", trackedDownload.Metadata?["TorrentHash"]?.ToString());
            Assert.Equal("HASH-ARTEMIS", persistedMetadata["ClientDownloadId"]?.ToString());
            Assert.Equal("HASH-ARTEMIS", persistedMetadata["TorrentHash"]?.ToString());
            downloadRepoMock.Verify(r => r.UpdateMetadataAsync("tracked-artemis", "ClientDownloadId", "HASH-ARTEMIS"), Times.Once);
            downloadRepoMock.Verify(r => r.UpdateMetadataAsync("tracked-artemis", "TorrentHash", "HASH-ARTEMIS"), Times.Once);
        }

        private static bool IsQueueDisplayCandidate(Download d)
        {
            bool isDdl = d.DownloadClientId == "DDL";
            bool notMoved = d.Status != DownloadStatus.Moved;
            bool notFailed = d.Status != DownloadStatus.Failed;
            bool notCompletedWithPath = d.Status != DownloadStatus.Completed || string.IsNullOrEmpty(d.FinalPath);
            return (isDdl && notMoved) || (!isDdl && notMoved && notFailed && notCompletedWithPath);
        }
    }
}
