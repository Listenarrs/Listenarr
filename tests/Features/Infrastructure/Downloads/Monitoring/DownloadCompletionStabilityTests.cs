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
using Listenarr.Tests.Mocks.DownloadClients;

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
            // lives only in the row's observation. If the client drops the item before the window
            // passes, a later poll never sees it complete again, and orphan cleanup would delete
            // the row. Without the window the import was enqueued on the first pass; the hold must
            // not do worse.
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

            Assert.NotNull((await _downloadRepository.GetByIdAsync(download.Id))!.GetCompletionObservedAt());

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            var inProgress = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotEqual(DownloadStatus.Completed, inProgress!.Status);
            // Restarting the window means dropping the observation, not keeping the old one: a
            // later completion report has to wait out a full window of its own.
            Assert.Null(inProgress.GetCompletionObservedAt());
            Assert.Null(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(download.Id));
        }

        [Fact]
        [Trait("Scenario", "Polls at unchanged telemetry keep counting from the first completion report")]
        public async Task CompletionWindow_DoesNotRestart_AcrossHoldCyclesAtUnchangedTelemetry()
        {
            // The hold cycles in the tests above mostly follow a poll that changed the row, so a
            // second write of the same row in one pass would be rejected by the client-poll guard
            // and go unnoticed. Here the client reports the same completed item on every poll, so
            // any extra write in a pass lands, and one that cleared the observation would restart
            // the window on every cycle and never release the download.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();
            var firstObserved = (await _downloadRepository.GetByIdAsync(download.Id))!.GetCompletionObservedAt();
            Assert.NotNull(firstObserved);

            for (var cycle = 0; cycle < 2; cycle++)
            {
                _clock.Advance(TimeSpan.FromSeconds(20));
                _monitor.ScheduleNextClientPoll(_client, -100);
                await _monitor.MonitorDownloadsAsync(CancellationToken.None);

                var held = await _downloadRepository.GetByIdAsync(download.Id);
                Assert.NotEqual(DownloadStatus.Completed, held!.Status);
                Assert.Equal(firstObserved, held.GetCompletionObservedAt());
            }

            _clock.Advance(TimeSpan.FromSeconds(21));
            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            Assert.Equal(DownloadStatus.Completed, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
        }

        [Fact]
        [Trait("Scenario", "A download the client did not list and that holds no completion is not written back")]
        public async Task UnlistedDownloadWithoutAnObservation_IsNotWrittenBack()
        {
            // Only a held completion the client stopped listing is added back to the pass. Any
            // other download the client left out has no new telemetry, and writing back the copy
            // loaded at the start of the pass would overwrite what another writer saved while the
            // poll was in flight. The progress change below is such a write; the client-poll guard
            // does not compare progress, so only leaving the row out of the pass keeps it.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var listed = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("1")
                .WithDownloadClientConfiguration(_client)
                .Build());
            var unlisted = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("2")
                .WithDownloadClientConfiguration(_client)
                .Build());

            var path = FileUtils.GetAbsolutePath(DownloadCLientAdapterMock.RemotePath, "random title");
            ClientAdapter().QueueItemsMock = [
                new QueueItemBuilder()
                    .WithId("1")
                    .WithRemotePath(path)
                    .WithContentPath(path)
                    .WithSourceFile(Path.Join(path, "file1.mp3"))
                    .WithProgress(50)
                    .WithStatus("downloading")
                    .Build()
            ];
            ClientAdapter().DuringFilteredQueueRequest = async () =>
            {
                var current = await _downloadRepository.GetByIdAsync(unlisted.Id);
                current!.Progress = 42;
                await _downloadRepository.UpdateAsync(current);
            };

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            // The control: the listed download in the same pass was written.
            Assert.Equal(50m, (await _downloadRepository.GetByIdAsync(listed.Id))!.Progress);
            var kept = await _downloadRepository.GetByIdAsync(unlisted.Id);
            Assert.NotNull(kept);
            Assert.Null(kept!.GetCompletionObservedAt());
            Assert.Equal(42m, kept.Progress);
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
            // An observation can outlive the hold, for example when the window is set to zero
            // while a download is held. If the row has meanwhile moved into import and the client then
            // drops the item, releasing it would write Completed over ImportPending, and the
            // Completed edge would enqueue the import a second time.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();

            var importing = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(importing!.GetCompletionObservedAt());
            await _downloadRepository.UpdateAsync(importing.Importing());

            ClientAdapter().QueueItemsMock = [];

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            Assert.Equal(DownloadStatus.ImportPending, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(download.Id));
        }

        [Fact]
        [Trait("Scenario", "A failed lookup does not release a held download the poll left out")]
        public async Task CompletionStaysHeld_WhenTheTrackedLookupFails()
        {
            // Absence from the poll is confirmed by looking the held download up in its client.
            // A lookup that fails says nothing about whether the item is there, so it must not be
            // read as the item being gone. The control is the release test above, which differs
            // only in the lookup succeeding.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();

            ClientAdapter().QueueItemsMock = [];
            ClientAdapter().TrackedQueueException = new DownloadClientAdapterPollingException("client unreachable");

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            var held = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotEqual(DownloadStatus.Completed, held!.Status);
            Assert.NotNull(held.GetCompletionObservedAt());
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(download.Id));
        }

        [Fact]
        [Trait("Scenario", "Only a held download the poll left out is looked up, and only that one")]
        public async Task TrackedLookup_AsksOnlyForTheHeldDownloadThePollLeftOut()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();

            // Still listed: the poll has evidence of its own, so nothing is looked up.
            var lookups = ClientAdapter().TrackedQueueRequestCount;
            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);
            Assert.Equal(lookups, ClientAdapter().TrackedQueueRequestCount);

            var other = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloading(0)
                .WithExternalId("2")
                .WithDownloadClientConfiguration(_client)
                .Build());
            var path = FileUtils.GetAbsolutePath(DownloadCLientAdapterMock.RemotePath, "random title two");
            ClientAdapter().QueueItemsMock = [
                new QueueItemBuilder()
                    .WithId("2")
                    .WithRemotePath(path)
                    .WithContentPath(path)
                    .WithSourceFile(Path.Join(path, "file1.mp3"))
                    .WithProgress(50)
                    .WithStatus("downloading")
                    .Build()
            ];

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            Assert.Equal(lookups + 1, ClientAdapter().TrackedQueueRequestCount);
            Assert.Equal(new[] { "1" }, ClientAdapter().LastTrackedQueueIds);
            Assert.Equal(DownloadStatus.Completed, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Equal(DownloadStatus.Downloading, (await _downloadRepository.GetByIdAsync(other.Id))!.Status);
        }

        [Fact]
        [Trait("Scenario", "A row that has moved into import is not looked up")]
        public async Task TrackedLookup_IsNotMadeForARowThatHasMovedIntoImport()
        {
            // The release only applies to a row still waiting for its first completion, so an
            // observation that outlived its hold has nothing to confirm and costs no request.
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds(60)
                .Build());

            var download = await DriveToCompletionAsync();
            var importing = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(importing!.GetCompletionObservedAt());
            await _downloadRepository.UpdateAsync(importing.Importing());

            ClientAdapter().QueueItemsMock = [];
            var lookups = ClientAdapter().TrackedQueueRequestCount;

            _monitor.ScheduleNextClientPoll(_client, -100);
            await _monitor.MonitorDownloadsAsync(CancellationToken.None);

            Assert.Equal(lookups, ClientAdapter().TrackedQueueRequestCount);
            Assert.Equal(DownloadStatus.ImportPending, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
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
