using System.Reflection;
using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
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
                .WithDownloading(50)
                .WithDownloadClientConfiguration(client)
                .Build());

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
        public async Task MonitorDownloadsAsync_RespectSchedulingInterval()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(50)
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Downloading, download.Status);
            Assert.True(download.Progress == 60);

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);
            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Downloading, download.Status);
            Assert.True(download.Progress == 60);

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var job = await downloadProcessingJobService.GetNextJobAsync();
            Assert.Null(job);
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorDownloadsAsync_DisabledClientDownload_DoesNotUpdate()
        {
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(50)
                .WithDownloadClientConfiguration(disabledClient)
                .Build());
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(50)
                .WithDownloadClientConfiguration(disabledClient)
                .Build());
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(50)
                .WithDownloadClientConfiguration(client)
                .Build());

            await downloadMonitorService.MonitorDownloadsAsync(CancellationToken.None);

            var downloads = await _downloadRepository.GetAllAsync();
            Assert.Equal(3, downloads.Count);

            foreach (Download download in downloads)
            {
                if (download.DownloadClientId == client.Id)
                {
                    Assert.NotEqual(50, download.Progress);
                }
                else
                {
                    Assert.Equal(50, download.Progress);
                    Assert.Equal(DownloadStatus.Downloading, download.Status);
                }
            }

            var downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
            var job = await downloadProcessingJobService.GetNextJobAsync();
            Assert.Null(job);
        }
    }
}
