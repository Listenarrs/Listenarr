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
using Listenarr.Infrastructure.Downloads.Monitoring;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent
{
    // The monitor cycle end to end against the qBittorrent API mock: the id-filtered poll that
    // updates tracked downloads, then orphan cleanup from a full snapshot, then the scheduling
    // of the next poll. Orphan cleanup only runs when the id-filtered poll succeeded, so a test
    // that calls the cleanup service directly cannot see whether a poll was counted as a
    // success or a failure. These can.
    [Trait("Name", "QbittorrentMonitorCycleTests")]
    [Trait("Category", "DownloadMonitorService")]
    public class QbittorrentMonitorCycleTests : BaseTests
    {
        // The first failure backs off 30 seconds and the second 60, each with up to 5 seconds of
        // jitter either way; a successful poll uses the 30 second default interval. Anything past
        // this threshold therefore means the failure count grew rather than reset.
        private static readonly TimeSpan SecondFailureBackoffFloor = TimeSpan.FromSeconds(45);

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
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorCycle_WhenOneTorrentIsUnreadable_KeepsItsTrackingRecord()
        {
            Api.InfoResponseOverride = QueueWithMiddleTorrentDownloaded("\"600\"");
            await TrackDownloadsAsync("aaaa1111", "bbbb2222", "cccc3333");

            await RunCycleAsync();

            Assert.NotNull(await _downloadRepository.GetByIdAsync("tracked-bbbb2222"));
        }

        // Control for the test above: the same cycle does run cleanup and remove a record whose
        // torrent is genuinely gone, so a surviving record is not just cleanup never running.
        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorCycle_WhenEveryTorrentIsReadable_RemovesAGenuineOrphan()
        {
            Api.InfoResponseOverride = QueueWithMiddleTorrentDownloaded("600");
            await TrackDownloadsAsync("aaaa1111", "bbbb2222", "cccc3333", "dddd4444");

            await RunCycleAsync();

            Assert.Null(await _downloadRepository.GetByIdAsync("tracked-dddd4444"));
            Assert.NotNull(await _downloadRepository.GetByIdAsync("tracked-bbbb2222"));
        }

        // Once the breaker is open, every poll is rejected partway through. Two such cycles in a
        // row must count as two failures, so the second backs off further than the first. If the
        // rejected files requests were swallowed as unreadable torrents, each cycle would count
        // as a success and the next poll would come round at the normal interval.
        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorCycle_WhenTheBreakerOpensMidPoll_BacksOffFurtherEachCycle()
        {
            Api.InfoResponseOverride = QueueWithMiddleTorrentDownloaded("600");
            Api.FilesRequestsBeforeBreakerOpens = 1;
            await TrackDownloadsAsync("aaaa1111", "bbbb2222", "cccc3333");

            await RunCycleAsync();
            Monitor.ScheduleNextClientPoll(_client, -100);
            await RunCycleAsync();

            Assert.True(
                TimeUntilNextPoll() > SecondFailureBackoffFloor,
                $"next poll in {TimeUntilNextPoll()}, expected the second failure's backoff");
            Assert.NotNull(await _downloadRepository.GetByIdAsync("tracked-bbbb2222"));
            Assert.NotNull(await _downloadRepository.GetByIdAsync("tracked-cccc3333"));
        }

        // Control for the backoff probe: two clean cycles keep the normal interval.
        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        public async Task MonitorCycle_WhenEveryPollSucceeds_KeepsTheNormalInterval()
        {
            Api.InfoResponseOverride = QueueWithMiddleTorrentDownloaded("600");
            await TrackDownloadsAsync("aaaa1111", "bbbb2222", "cccc3333");

            await RunCycleAsync();
            Monitor.ScheduleNextClientPoll(_client, -100);
            await RunCycleAsync();

            Assert.True(
                TimeUntilNextPoll() < SecondFailureBackoffFloor,
                $"next poll in {TimeUntilNextPoll()}, expected the normal interval");
        }

        private QbittorrentApiMock Api => _provider.GetRequiredService<QbittorrentApiMock>();

        private DownloadMonitorService Monitor => _provider.GetRequiredService<DownloadMonitorService>();

        private Task RunCycleAsync() => Monitor.MonitorDownloadsAsync(CancellationToken.None);

        private TimeSpan TimeUntilNextPoll() =>
            _provider.GetRequiredService<DownloadMonitorProcessor>()._nextClientPoll[_client.Id] - DateTime.UtcNow;

        // Three torrents; the middle one carries `downloaded` in the given JSON token form.
        private static string QueueWithMiddleTorrentDownloaded(string downloaded) => $$"""
        [
            {
                "hash": "aaaa1111", "name": "First", "progress": 0.5, "size": 1000,
                "downloaded": 500, "state": "downloading", "save_path": "/downloads/a"
            },
            {
                "hash": "bbbb2222", "name": "Second", "progress": 0.5, "size": 1000,
                "downloaded": {{downloaded}}, "state": "downloading", "save_path": "/downloads/b"
            },
            {
                "hash": "cccc3333", "name": "Third", "progress": 0.5, "size": 1000,
                "downloaded": 700, "state": "downloading", "save_path": "/downloads/c"
            }
        ]
        """;

        // Tracked downloads old enough that orphan cleanup's grace period no longer protects them.
        private async Task TrackDownloadsAsync(params string[] hashes)
        {
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
                await _downloadRepository.AddAsync(download);
            }
        }
    }
}
