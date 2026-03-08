using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Repositories;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
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
            IAppMetricsService metrics)
        {
            var memoryCache = Track(new MemoryCache(new MemoryCacheOptions()));
            var pathMapping = new Mock<IRemotePathMappingService>();
            var httpFactory = new Mock<IHttpClientFactory>();
            var httpClient = Track(new HttpClient());
            httpFactory.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var scopeProvider = Track(new ServiceCollection().BuildServiceProvider());
            var scopeFactory = scopeProvider.GetRequiredService<IServiceScopeFactory>();

            return new DownloadQueueService(
                memoryCache,
                configurationService,
                downloadRepository,
                processingJobRepository,
                clientGateway,
                pathMapping.Object,
                httpFactory.Object,
                scopeFactory,
                metrics,
                NullLogger<DownloadQueueService>.Instance);
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
            downloadRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Download> { dbTitleMatch, dbIdMatch });

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
        [Trait("Scenario", "MissingSnapshotRetainsTrackedDownload")]
        public async Task GetQueueAsync_MissingQueueSnapshot_RetainsTrackedRecord_AndEmitsMetric()
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
            downloadRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Download> { tracked });

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
            metricsMock.Verify(m => m.Increment("download.purge.skipped.tracked_orphan_retained", 1), Times.Once);
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
            downloadRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Download>());

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
            downloadRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Download> { trackedDownload });

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
    }
}
