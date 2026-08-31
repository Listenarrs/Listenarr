using System.Reflection;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Monitoring
{
    [Trait("Name", "DownloadMonitorServiceTests")]
    [Trait("Category", "DownloadMonitorService")]
    public class DownloadMonitorServiceTests : BaseTests
    {
        private DownloadMonitorService downloadMonitorService = null!;
        private MethodInfo monitorDownloadsAsync = null!;
        private DownloadClientConfiguration client = null!;
        private DownloadClientConfiguration disabledClient = null!;

        public override async Task InitializeAsync()
        {
            downloadMonitorService = _provider.GetRequiredService<DownloadMonitorService>();
            var method = typeof(DownloadMonitorService).GetMethod("MonitorDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            monitorDownloadsAsync = method;

            // These poll in a tight loop on the real clock and assert completion, so they need
            // the completion stability window out of the way. Stated rather than inherited.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutCompletionStabilityWindow()
                .Build());

            client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithType("mock")
                .WithName("Mock")
                .Build());

            disabledClient = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithType("mock")
                .WithName("Mock")
                .WithDisabled()
                .Build());
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_NoDownload_NoResult()
        {
            var downloads = await _downloadRepository.GetAllAsync();
            Assert.Empty(downloads);

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            downloads = await _downloadRepository.GetAllAsync();
            Assert.Empty(downloads);

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var job = await downloadProcessingJobService.GetNextJobAsync();
            Assert.Null(job);
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_CompletedStaysCompleted()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Completed, download.Status);

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var job = await downloadProcessingJobService.GetNextJobAsync();
            Assert.Null(job);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Completed, download.Status);
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_DownloadingBecomesCompleted()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("1")
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Downloading, download.Status);
            Assert.True(download.Progress > 50);

            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Downloading, download.Status);
            Assert.True(download.Progress > 80);

            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Completed, download.Status);
            Assert.True(download.Progress >= 100);

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var job = await downloadProcessingJobService.GetNextJobAsync();
            Assert.NotNull(job);
            Assert.Equal(download.Id, job.DownloadId);
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_LiveSnapshot_RemovesEligibleOrphanedDownload()
        {
            var orphan = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("orphaned-download")
                .WithDownloading(0)
                .WithExternalId("missing-client-id")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-10))
                .WithDownloadClientConfiguration(client)
                .Build());
            var present = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("present-download")
                .WithDownloading(0)
                .WithExternalId("1")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-10))
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(orphan.Id));
            Assert.NotNull(await _downloadRepository.GetByIdAsync(present.Id));
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_LiveSnapshot_RemovesOldActiveClientDownloadWithoutExternalId()
        {
            var unlinked = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("unlinked-download")
                .WithDownloading(0)
                .WithStartDate(DateTime.UtcNow.AddMinutes(-10))
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(unlinked.Id));
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_OrphanCleanup_DoesNotRunEveryCycle()
        {
            var adapter = _provider.GetServices<IDownloadClientAdapter>()
                .OfType<DownloadCLientAdapterMock>()
                .Single();
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("1")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-10))
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            Assert.Equal(2, adapter.FilteredQueueRequestCount);
            Assert.Equal(1, adapter.FullSnapshotQueueRequestCount);
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_OrphanCleanup_RunsAgainAfterThrottleInterval()
        {
            var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
            Init(services => services.WithSingleton<TimeProvider>(timeProvider));
            downloadMonitorService = _provider.GetRequiredService<DownloadMonitorService>();
            client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithType("mock")
                .WithName("Mock")
                .Build());
            var adapter = _provider.GetServices<IDownloadClientAdapter>()
                .OfType<DownloadCLientAdapterMock>()
                .Single();
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("1")
                .WithStartDate(DateTime.UtcNow.AddMinutes(-10))
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            timeProvider.Advance(TimeSpan.FromMinutes(9));
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            timeProvider.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
            downloadMonitorService.ScheduleNextClientPoll(client, -100);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            Assert.Equal(3, adapter.FilteredQueueRequestCount);
            Assert.Equal(2, adapter.FullSnapshotQueueRequestCount);
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_RespectSchedulingInterval()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("1")
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Downloading, download.Status);
            Assert.True(download.Progress == 10);

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Downloading, download.Status);
            Assert.True(download.Progress == 10);

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var job = await downloadProcessingJobService.GetNextJobAsync();
            Assert.Null(job);
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_DisabledClientDownload_DoesNotUpdate()
        {
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("DISABLED_1")
                .WithDownloadClientConfiguration(disabledClient)
                .Build());
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("DISABLED_2")
                .WithDownloadClientConfiguration(disabledClient)
                .Build());
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("1")
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            var downloads = await _downloadRepository.GetAllAsync();
            Assert.Equal(3, downloads.Count);

            foreach (Download download in downloads)
            {
                if (download.DownloadClientId == client.Id)
                {
                    Assert.Equal(10, download.Progress);
                }
                else
                {
                    Assert.Equal(0, download.Progress);
                    Assert.Equal(DownloadStatus.Downloading, download.Status);
                }
            }

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var job = await downloadProcessingJobService.GetNextJobAsync();
            Assert.Null(job);
        }

        [Fact]
        public void OnDownloadFailed_Nzbget_DoesNotRemoveClientHistory_WhenFailedHandlingEnabled()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "nzbget-1",
                Type = "nzbget"
            };

            Assert.False(DownloadMonitorProcessor.ShouldRemoveFailedClientItem(client));
        }

        [Fact]
        public void OnDownloadFailed_Qbittorrent_RemovesFailedClientItem_WhenFailedHandlingEnabled()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "qbittorrent-1",
                Type = "qbittorrent"
            };

            Assert.True(DownloadMonitorProcessor.ShouldRemoveFailedClientItem(client));
        }

        [Fact]
        public void OnDownloadFailed_NzbgetMoveFailure_SuppressesImmediateAutoSearch()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "nzbget-1",
                Type = "nzbget"
            };
            var download = new DownloadBuilder().Build();
            download.Metadata["ClientFailureReason"] = "FAILURE/MOVE";

            Assert.True(DownloadMonitorProcessor.ShouldSuppressFailedDownloadAutoSearch(
                client,
                download,
                "NZBGet failed during post-processing or final move."));
        }

        [Fact]
        public void OnDownloadFailed_NzbgetUnpackFailure_AllowsImmediateAutoSearch()
        {
            var client = new DownloadClientConfiguration
            {
                Id = "nzbget-1",
                Type = "nzbget"
            };
            var download = new DownloadBuilder().Build();
            download.Metadata["ClientFailureReason"] = "FAILURE/UNPACK";

            Assert.False(DownloadMonitorProcessor.ShouldSuppressFailedDownloadAutoSearch(
                client,
                download,
                "NZBGet failed while unpacking."));
        }

        private sealed class MutableTimeProvider(DateTimeOffset currentTime) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => currentTime;

            public void Advance(TimeSpan value) => currentTime = currentTime.Add(value);
        }
    }
}
