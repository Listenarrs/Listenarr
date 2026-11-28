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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.DownloadClients;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Transmission
{
    // The completion stability window end to end against the Transmission RPC mock. The monitor
    // poll skips a torrent it cannot map without saying so, so the poll cannot tell a skipped
    // torrent from a removed one; only the tracked lookup of the held torrent can.
    [Trait("Name", "TransmissionCompletionHoldTests")]
    [Trait("Category", "DownloadMonitorService")]
    [Trait("Third-Party", "Transmission")]
    public class TransmissionCompletionHoldTests : BaseTests
    {
        private const string HeldHash = "aaaa1111";
        private const string OtherHash = "bbbb2222";
        private static readonly TimeSpan StabilityWindow = TimeSpan.FromSeconds(120);

        private readonly AdjustableTimeProvider _clock = new();
        private DownloadClientConfiguration _client = null!;

        public TransmissionCompletionHoldTests()
        {
            Init(builder => builder.WithSingleton<TimeProvider>(_clock));
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithName("Transmission")
                .WithType("transmission")
                .WithHost("localhost")
                .WithPort(9091)
                .Build());
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds((int)StabilityWindow.TotalSeconds)
                .Build());
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        [Trait("Scenario", "A held torrent the poll cannot read is not released early")]
        public async Task HeldCompletion_StillListedButUnreadable_StaysHeld()
        {
            // The reported case: the torrent stays in the response but its eta is null, so the
            // mapper skips it. The poll then looked complete and the next cycle queued the import
            // before the window had passed.
            await HoldCompletionAsync();

            Api.FullQueueResponseOverride = Queue(Torrent(HeldHash, complete: true, eta: "null"), Torrent(OtherHash, complete: false));
            _clock.Advance(TimeSpan.FromSeconds(30));
            await RunCycleAsync();

            Assert.Equal(DownloadStatus.Downloading, (await HeldDownloadAsync())!.Status);
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(HeldId));
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        [Trait("Scenario", "A held torrent removed from the client is released")]
        public async Task HeldCompletion_RemovedFromTheClient_IsFinalized()
        {
            // The control for the test above: the same cycle with the torrent genuinely removed
            // releases it, so a hold that survives is the lookup and not a harness that never
            // releases anything.
            await HoldCompletionAsync();

            Api.FullQueueResponseOverride = Queue(Torrent(OtherHash, complete: false));
            _clock.Advance(TimeSpan.FromSeconds(30));
            await RunCycleAsync();

            Assert.Equal(DownloadStatus.Completed, (await HeldDownloadAsync())!.Status);
            Assert.NotNull(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(HeldId));
        }

        private static string HeldId => $"tracked-{HeldHash}";

        private TransmissionApiMock Api => _provider.GetRequiredService<TransmissionApiMock>();

        private DownloadMonitorProcessor Processor => _provider.GetRequiredService<DownloadMonitorProcessor>();

        private Task<Download?> HeldDownloadAsync() => _downloadRepository.GetByIdAsync(HeldId);

        private async Task RunCycleAsync()
        {
            Processor.ScheduleNextClientPoll(_client, -100);
            await Processor.RunCycleAsync(CancellationToken.None);
        }

        // The held torrent reports complete on a first cycle and is held, with another torrent
        // in progress beside it so that a response without the held one is not empty.
        private async Task HoldCompletionAsync()
        {
            await TrackAsync(HeldHash);
            await TrackAsync(OtherHash);
            Api.FullQueueResponseOverride = Queue(Torrent(HeldHash, complete: true), Torrent(OtherHash, complete: false));

            await RunCycleAsync();

            Assert.Equal(DownloadStatus.Downloading, (await HeldDownloadAsync())!.Status);
            Assert.NotNull((await HeldDownloadAsync())!.GetCompletionObservedAt());
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(HeldId));
        }

        // Old enough that orphan cleanup's grace period no longer protects it.
        private async Task TrackAsync(string hash)
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

        private static string Queue(params string[] torrents) =>
            $$"""{ "arguments": { "torrents": [{{string.Join(",", torrents)}}] }, "result": "success", "tag": 3 }""";

        // Status 6 is seeding, which the mapper reads as complete once percentDone reaches 1.
        // Gateway-level reads reject a download directory that is not valid on the host, so the
        // fixtures use host paths (JSON-encoded, as a Windows path carries backslashes).
        private static string Torrent(string hash, bool complete, string eta = "-1") => $$"""
            {
                "id": {{(hash == HeldHash ? 1 : 2)}}, "hashString": "{{hash}}", "name": "{{hash}}",
                "percentDone": {{(complete ? "1" : "0.5")}}, "status": {{(complete ? 6 : 4)}},
                "totalSize": 1000, "leftUntilDone": {{(complete ? 0 : 500)}}, "eta": {{eta}},
                "downloadDir": {{JsonSerializer.Serialize(FileUtils.GetAbsolutePath("downloads"))}}
            }
            """;
    }
}
