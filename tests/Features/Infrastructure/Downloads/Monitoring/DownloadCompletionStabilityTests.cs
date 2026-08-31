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
    }
}
