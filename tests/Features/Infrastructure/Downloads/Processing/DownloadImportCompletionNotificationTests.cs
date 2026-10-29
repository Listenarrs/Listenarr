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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Processing
{
    /// <summary>
    /// Covers the completion notification for a download imported by the processing queue.
    /// </summary>
    /// <remarks>
    /// The queue commits a finished import through the finalization service, which writes the Moved
    /// status straight to the database and so never passes the status-change hook in the download
    /// service. These tests drive the real processor over real files so that a completion event can
    /// only be observed if finalization itself emits it.
    /// </remarks>
    [Trait("Name", "DownloadImportCompletionNotificationTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public sealed class DownloadImportCompletionNotificationTests : BaseTests
    {
        private readonly DownloadClientGatewayMock _downloadClientGateway = new();
        private readonly RecordingNotificationService _notifications = new();
        private DownloadProcessingJobProcessor _processor = null!;
        private IDownloadProcessingJobService _jobService = null!;

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IDownloadClientGateway>(_downloadClientGateway);
            _services.Replace(ServiceDescriptor.Scoped<INotificationService>(_ => _notifications));
            Init();
            await AddAuthorizedRootAsync(FileService.GetTempPath());
            _processor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            _jobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
        }

        public static TheoryData<FileAction> SupportedFileActions()
            => [FileAction.Move, FileAction.Copy, FileAction.HardlinkCopy];

        [Theory]
        [MemberData(nameof(SupportedFileActions))]
        public async Task SuccessfulImport_EmitsOneCompletionOnceTheImportIsCommitted(FileAction fileAction)
        {
            var (download, jobId) = await EnqueueImportAsync(fileAction);

            await _processor.ProcessQueueAsync(CancellationToken.None);

            var committed = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.Equal(DownloadStatus.Moved, committed!.Status);
            Assert.Equal(ProcessingJobStatus.Completed, (await _downloadProcessingJobRepository.GetByIdAsync(jobId))!.Status);
            Assert.Equal([download.Id], _notifications.Imported);
            Assert.Empty(_notifications.Failed);
        }

        [Fact]
        public async Task ImportThatNeedsARetry_EmitsNothingUntilTheRetryCommits()
        {
            var (download, jobId) = await EnqueueImportAsync(FileAction.Copy);
            _downloadClientGateway.MarkImportedResult = false;

            await _processor.ProcessQueueAsync(CancellationToken.None);

            var retrying = (await _downloadProcessingJobRepository.GetByIdAsync(jobId))!;
            Assert.Equal(ProcessingJobStatus.Pending, retrying.Status);
            Assert.Empty(_notifications.Imported);

            _downloadClientGateway.MarkImportedResult = true;
            retrying.NextRetryAt = DateTime.UtcNow.AddSeconds(-1);
            await _downloadProcessingJobRepository.UpdateAsync(retrying);

            await _processor.ProcessQueueAsync(CancellationToken.None);

            Assert.Equal(DownloadStatus.Moved, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Equal([download.Id], _notifications.Imported);
        }

        [Fact]
        public async Task FailedImport_EmitsTheFailureAndNoCompletion()
        {
            var (download, jobId) = await EnqueueImportAsync(FileAction.Copy);
            _downloadClientGateway.SourceFiles = [Path.Join(FileService.GetTempDirectory("dl-missing"), "missing.m4b")];
            var job = (await _downloadProcessingJobRepository.GetByIdAsync(jobId))!;
            job.MaxRetries = 0;
            await _downloadProcessingJobRepository.UpdateAsync(job);

            await _processor.ProcessQueueAsync(CancellationToken.None);

            Assert.Equal(ProcessingJobStatus.Failed, (await _downloadProcessingJobRepository.GetByIdAsync(jobId))!.Status);
            Assert.NotEqual(DownloadStatus.Moved, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Empty(_notifications.Imported);
            Assert.Equal([download.Id], _notifications.Failed);
        }

        [Fact]
        public async Task CompletedJobRunAgain_DoesNotEmitASecondCompletion()
        {
            var (download, jobId) = await EnqueueImportAsync(FileAction.Copy);
            await _processor.ProcessQueueAsync(CancellationToken.None);
            Assert.Equal([download.Id], _notifications.Imported);

            var completed = (await _downloadProcessingJobRepository.GetByIdAsync(jobId))!;
            completed.Status = ProcessingJobStatus.Pending;
            completed.NextRetryAt = null;
            await _downloadProcessingJobRepository.UpdateAsync(completed);

            await _processor.ProcessQueueAsync(CancellationToken.None);

            Assert.Equal(ProcessingJobStatus.Completed, (await _downloadProcessingJobRepository.GetByIdAsync(jobId))!.Status);
            Assert.Equal([download.Id], _notifications.Imported);
        }

        [Fact]
        public async Task FinalizingAnAlreadyCommittedImport_DoesNotEmitASecondCompletion()
        {
            var (download, jobId) = await EnqueueImportAsync(FileAction.Copy);
            await _processor.ProcessQueueAsync(CancellationToken.None);
            var audiobook = (await _audiobookRepository.GetByIdAsync(download.AudiobookId!.Value))!;

            using var scope = _provider.CreateScope();
            var finalization = scope.ServiceProvider.GetRequiredService<IImportFinalizationService>();
            await finalization.FinalizeAsync(
                jobId, download.Id, audiobook.Id, audiobook.Title ?? download.Title,
                download.DownloadClientId!, "finalization-repeat", sourceRetained: true);

            Assert.Equal([download.Id], _notifications.Imported);
        }

        private async Task<(Download Download, string JobId)> EnqueueImportAsync(FileAction fileAction)
        {
            var sourceDir = FileService.GetTempDirectory("dl-dir");
            var destRoot = FileService.GetTempDirectory("dl-dest");
            _downloadClientGateway.SourceFiles = [await FileService.GetFileAsync(sourceDir, "book.m4b")];

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destRoot)
                .Build());
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithAudiobook(audiobook)
                .Build());

            var settings = fileAction switch
            {
                FileAction.Move => new ApplicationSettingsBuilder().WithMoveFileOnCompleted(),
                FileAction.Copy => new ApplicationSettingsBuilder().WithCopyFileOnCompleted(),
                FileAction.HardlinkCopy => new ApplicationSettingsBuilder().WithHardlinkFileOnCompleted(),
                _ => throw new ArgumentOutOfRangeException(nameof(fileAction), fileAction, "Not an import file action")
            };
            await _applicationSettingsRepository.SaveAsync(settings.WithoutMetadataProcessing().Build());

            var jobId = await _jobService.EnqueueAsync(download);
            return (download, jobId);
        }

        /// <summary>Records which downloads each typed notification was raised for.</summary>
        private sealed class RecordingNotificationService : INotificationService
        {
            public List<string> Imported { get; } = [];

            public List<string> Failed { get; } = [];

            public Task OnDownloadImportedAsync(Download download)
            {
                Imported.Add(download.Id);
                return Task.CompletedTask;
            }

            public Task OnDownloadFailedAsync(Download download)
            {
                Failed.Add(download.Id);
                return Task.CompletedTask;
            }

            public Task SendSystemNotificationAsync(string title, string message) => Task.CompletedTask;

            public Task SendNotificationAsync(string trigger, object data) => Task.CompletedTask;

            public Task SendNotificationAsync(string trigger, object data, string webhookUrl, List<string> enabledTriggers)
                => Task.CompletedTask;
        }
    }
}
