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
using Listenarr.Domain.Downloads.Exceptions;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Monitoring
{
    [Trait("Name", "DownloadCompletionStabilityTests")]
    [Trait("Category", "DownloadMonitorService")]
    public class DownloadCompletionStabilityTests : BaseTests
    {
        private readonly AdjustableTimeProvider _clock = new();
        private DownloadMonitorService _monitor = null!;
        private DownloadClientConfiguration _client = null!;

        public DownloadCompletionStabilityTests()
        {
            Init(builder => builder.WithSingleton<TimeProvider>(_clock));
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            _monitor = _provider.GetRequiredService<DownloadMonitorService>();
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithType("mock")
                .WithName("Mock")
                .Build());
        }

        private async Task<Download> DriveToCompletionAsync()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("1")
                .WithDownloadClientConfiguration(_client)
                .Build());

            // The mock client walks progress up on each poll and reports completion at the end.
            for (var poll = 0; poll < 12; poll++)
            {
                _monitor.ScheduleNextClientPoll(_client, -100);
                await _monitor.MonitorDownloadsAsync(CancellationToken.None);
            }

            return download;
        }

        [Fact]
        [Trait("Scenario", "A configured stability window holds finalization back")]
        public async Task CompletionIsHeld_WhileTheStabilityWindowHasNotPassed()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();

            var held = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(held);
            Assert.NotEqual(DownloadStatus.Completed, held!.Status);

            // Held, not dropped: the row is still being updated, so progress is current even while
            // finalization waits.
            Assert.True(held.Progress >= 100);
        }

        [Fact]
        [Trait("Scenario", "The transition is let through once the window has passed")]
        public async Task CompletionProceeds_OnceTheStabilityWindowHasPassed()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();
            Assert.NotEqual(DownloadStatus.Completed, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);

            _clock.Advance(TimeSpan.FromSeconds(61));
            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            var released = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(released);
            Assert.Equal(DownloadStatus.Completed, released!.Status);
        }

        [Fact]
        [Trait("Scenario", "A held download that vanishes from its client is finalized, not lost")]
        public async Task CompletionProceeds_WhenAHeldDownloadDisappearsFromTheClient()
        {
            // The hold writes the row back with its pre-completion status, so the completion edge
            // lives only in memory. If the client drops the item before the window passes, a later
            // poll never sees it complete again, and orphan cleanup would delete the row. Without
            // the window the import was enqueued on the first pass; the hold must not do worse.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();
            Assert.NotEqual(DownloadStatus.Completed, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Null(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(download.Id));

            ClientAdapter().QueueItemsMock = [];

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            var finalized = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(finalized);
            Assert.Equal(DownloadStatus.Completed, finalized!.Status);
            Assert.NotNull(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(download.Id));
        }

        [Fact]
        [Trait("Scenario", "A held download the client still lists as in progress keeps waiting")]
        public async Task CompletionStaysHeld_WhenTheClientReportsTheDownloadInProgressAgain()
        {
            // The control for the test above: an item the client still lists, but no longer as
            // complete (a recheck, or post-processing), restarts the window rather than being
            // finalized. Without this, finalizing every held download on its next poll would pass.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();

            var path = FileUtils.GetAbsolutePath(DownloadCLientAdapterMock.RemotePath, "random title");
            ClientAdapter().QueueItemsMock = [
                new QueueItemBuilder()
                    .WithId("1")
                    .WithRemotePath(path)
                    .WithContentPath(path)
                    .WithSourceFile(Path.Join(path, "file1.mp3"))
                    .WithProgress(99)
                    .WithStatus("downloading")
                    .Build()
            ];

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            Assert.NotEqual(DownloadStatus.Completed, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Null(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(download.Id));
        }

        [Fact]
        [Trait("Scenario", "A failed poll does not release a held download")]
        public async Task CompletionStaysHeld_WhenThePollFails()
        {
            // The second control for the release above. A poll that fails says nothing about
            // whether the client still has the item, so it must not be read as the item being
            // gone. The monitor abandons the client group on a polling failure, which leaves the
            // hold exactly as it was. The empty list is set as well, so that the exception is the
            // only difference from the release test: if it were not thrown, this would finalize.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();

            ClientAdapter().QueueItemsMock = [];
            ClientAdapter().FilteredQueueException = new DownloadClientAdapterPollingException("client unreachable");

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            Assert.NotEqual(DownloadStatus.Completed, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(download.Id));
        }

        [Fact]
        [Trait("Scenario", "The release only applies to a row still waiting for its first completion")]
        public async Task GoneFromClient_DoesNotRestartAnImportThatHasMovedOn()
        {
            // A hold entry can outlive the hold, for example when the window is set to zero while
            // a download is held. If the row has meanwhile moved into import and the client then
            // drops the item, releasing it would write Completed over ImportPending, and the
            // Completed edge would enqueue the import a second time.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();
            var processor = (DownloadMonitorProcessor)_provider.GetRequiredService<IDownloadMonitorProcessor>();
            Assert.True(processor._completionFirstSeen.ContainsKey(download.Id));

            var importing = await _downloadRepository.GetByIdAsync(download.Id);
            await _downloadRepository.UpdateAsync(importing!.Importing());

            ClientAdapter().QueueItemsMock = [];

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            Assert.Equal(DownloadStatus.ImportPending, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(download.Id));
        }

        private DownloadCLientAdapterMock ClientAdapter() =>
            _provider.GetServices<IDownloadClientAdapter>()
                .OfType<DownloadCLientAdapterMock>()
                .Single();

        [Fact]
        [Trait("Scenario", "A zero window finalizes in the same pass, as before")]
        public async Task CompletionIsImmediate_WhenTheWindowIsZero()
        {
            // The control. Without this, a test asserting the hold would also pass against an
            // implementation that simply never finalizes.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutCompletionStabilityWindow()
                .Build());

            var download = await DriveToCompletionAsync();

            var finalized = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(finalized);
            Assert.Equal(DownloadStatus.Completed, finalized!.Status);
        }

        [Theory]
        [Trait("Scenario", "Only a first transition into Completed enters the window")]
        [InlineData(DownloadStatus.Queued, true)]
        [InlineData(DownloadStatus.Downloading, true)]
        [InlineData(DownloadStatus.Paused, true)]
        [InlineData(DownloadStatus.Processing, true)]
        [InlineData(DownloadStatus.Ready, true)]
        [InlineData(DownloadStatus.Failed, true)]
        [InlineData(DownloadStatus.Completed, false)]
        [InlineData(DownloadStatus.ImportPending, false)]
        [InlineData(DownloadStatus.ImportBlocked, false)]
        [InlineData(DownloadStatus.Moved, false)]
        public void OnlyPreCompletionStatusesEnterTheWindow(DownloadStatus previousStatus, bool expected)
        {
            // The four false rows are the ones that matter. EfDownloadRepository.GetActiveAsync
            // returns Completed, ImportPending and Moved rows on every cycle, and a guard that
            // only asked whether the previous status was Completed pulled the other two into the
            // window, so a row already in import had its status written back for as long as the
            // window lasted. Nothing outside a first completion should be held.
            Assert.Equal(expected, DownloadMonitorProcessor.IsPreCompletion(previousStatus));
        }
    }
}
