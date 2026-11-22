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

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent
{
    // The completion stability window end to end against the qBittorrent API mock: a held
    // completion must survive a restart, and an omitted torrent may only be treated as removed
    // when the poll that omitted it accounted for every torrent the client listed.
    [Trait("Name", "QbittorrentCompletionHoldTests")]
    [Trait("Category", "DownloadMonitorService")]
    public class QbittorrentCompletionHoldTests : BaseTests
    {
        private const string HeldHash = "aaaa1111";
        private const string OtherHash = "bbbb2222";
        private static readonly TimeSpan StabilityWindow = TimeSpan.FromSeconds(120);

        private readonly AdjustableTimeProvider _clock = new();
        private readonly HoldLogRecorder _logs = new();
        private DownloadClientConfiguration _client = null!;

        public QbittorrentCompletionHoldTests()
        {
            Init(builder => builder
                .WithSingleton<TimeProvider>(_clock)
                .WithSingleton<ILoggerProvider>(_logs));
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("localhost")
                .WithPort(443)
                .WithSsl()
                .WithUsername("admin")
                .WithPassword("admin")
                .WithType("qbittorrent")
                .Build());
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCompletionStabilitySeconds((int)StabilityWindow.TotalSeconds)
                .Build());
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        [Trait("Scenario", "A torrent left out of an incomplete poll is not released early")]
        public async Task HeldCompletion_OmittedByAnIncompletePoll_StaysHeld()
        {
            // The reported case: the torrent is still listed, but its files response cannot be
            // parsed, so the adapter leaves it out and marks the poll incomplete. Reading that
            // omission as removal queued the import before the window had passed.
            await HoldCompletionAsync();

            Api.MalformedFilesForHash = HeldHash;
            _clock.Advance(TimeSpan.FromSeconds(30));
            await RunCycleAsync(Processor);

            Assert.Equal(DownloadStatus.Downloading, (await HeldDownloadAsync())!.Status);
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(HeldId));
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        [Trait("Scenario", "An overdue hold kept by an incomplete poll is logged once")]
        public async Task HeldCompletion_OverdueAndKeptByAnIncompletePoll_LogsOnceAndStaysHeld()
        {
            await HoldCompletionAsync();
            Api.MalformedFilesForHash = HeldHash;

            // Inside the window the hold is not overdue, so nothing is logged: the control for
            // the count below.
            _clock.Advance(TimeSpan.FromSeconds(30));
            await RunCycleAsync(Processor);
            Assert.Equal(0, _logs.Count(OverdueHoldLine));

            _clock.Advance(StabilityWindow);
            await RunCycleAsync(Processor);
            _clock.Advance(TimeSpan.FromSeconds(30));
            await RunCycleAsync(Processor);

            Assert.Equal(1, _logs.Count(OverdueHoldLine));
            Assert.Equal(DownloadStatus.Downloading, (await HeldDownloadAsync())!.Status);
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(HeldId));
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        [Trait("Scenario", "A new hold that goes overdue logs again")]
        public async Task HeldCompletion_NewHoldOverdueAfterTheFirstEnded_LogsAgain()
        {
            // The once-per-hold guard must end with the hold. A client that stops reporting the
            // torrent complete clears the observation; when it completes again that is a new hold.
            await HoldCompletionAsync();
            Api.MalformedFilesForHash = HeldHash;
            _clock.Advance(StabilityWindow + TimeSpan.FromSeconds(1));
            await RunCycleAsync(Processor);
            Assert.Equal(1, _logs.Count(OverdueHoldLine));

            Api.MalformedFilesForHash = null;
            Api.InfoResponseOverride = Queue(Torrent(HeldHash, complete: false), Torrent(OtherHash, complete: false));
            await RunCycleAsync(Processor);

            Api.InfoResponseOverride = Queue(Torrent(HeldHash, complete: true), Torrent(OtherHash, complete: false));
            await RunCycleAsync(Processor);

            Api.MalformedFilesForHash = HeldHash;
            _clock.Advance(StabilityWindow + TimeSpan.FromSeconds(1));
            await RunCycleAsync(Processor);

            Assert.Equal(2, _logs.Count(OverdueHoldLine));
            Assert.Equal(DownloadStatus.Downloading, (await HeldDownloadAsync())!.Status);
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        [Trait("Scenario", "A torrent gone from a complete poll is released")]
        public async Task HeldCompletion_GoneFromACompletePoll_IsFinalized()
        {
            // The control for the test above: the same cycle with the torrent genuinely removed
            // releases it, so a hold that survives is the completeness check and not a harness
            // that never releases anything.
            await HoldCompletionAsync();

            Api.InfoResponseOverride = Queue(Torrent(OtherHash, complete: false));
            _clock.Advance(TimeSpan.FromSeconds(30));
            await RunCycleAsync(Processor);

            Assert.Equal(DownloadStatus.Completed, (await HeldDownloadAsync())!.Status);
            Assert.NotNull(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(HeldId));
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        [Trait("Scenario", "An unreadable torrent does not keep a removed one held")]
        public async Task HeldCompletion_GoneWhileAnotherTorrentIsUnreadable_IsFinalized()
        {
            // Another torrent the poll cannot read used to mark the whole poll incomplete, which
            // kept this removed torrent held for as long as the other one stayed unreadable. The
            // lookup of the held torrent itself says it is gone.
            await HoldCompletionAsync();

            Api.InfoResponseOverride = Queue(Torrent(OtherHash, complete: false));
            Api.MalformedFilesForHash = OtherHash;
            _clock.Advance(TimeSpan.FromSeconds(30));
            await RunCycleAsync(Processor);

            Assert.Equal(DownloadStatus.Completed, (await HeldDownloadAsync())!.Status);
            Assert.NotNull(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(HeldId));
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        [Trait("Scenario", "A held torrent removed while Listenarr was stopped is finalized after restart")]
        public async Task HeldCompletion_TorrentRemovedAcrossARestart_IsFinalizedNotDeleted()
        {
            // The reported case: stopped during the hold, the torrent removed from the client,
            // restarted. The first cycle after the restart runs orphan cleanup, and with the
            // observation held only in memory it deleted the row while the audio stayed on disk.
            await HoldCompletionAsync();

            Api.InfoResponseOverride = Queue(Torrent(OtherHash, complete: false));
            await RunCycleAsync(RestartedProcessor());

            var finalized = await HeldDownloadAsync();
            Assert.NotNull(finalized);
            Assert.Equal(DownloadStatus.Completed, finalized!.Status);
            Assert.NotNull(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(HeldId));
        }

        [Fact]
        [Trait("Method", "MonitorDownloadsAsync")]
        [Trait("Scenario", "The window keeps counting from the first observation across a restart")]
        public async Task HeldCompletion_StillCompleteAfterARestart_IsReleasedOnTheOriginalWindow()
        {
            // Recovery reads the persisted observation rather than starting a new one, so a
            // restart does not push finalization back by a whole window.
            await HoldCompletionAsync();

            _clock.Advance(StabilityWindow + TimeSpan.FromSeconds(1));
            await RunCycleAsync(RestartedProcessor());

            Assert.Equal(DownloadStatus.Completed, (await HeldDownloadAsync())!.Status);
            Assert.NotNull(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(HeldId));
        }

        [Fact]
        [Trait("Method", "RemoveOrphansAsync")]
        [Trait("Scenario", "Orphan cleanup leaves a row whose completion is pending finalization")]
        public async Task OrphanCleanup_HeldCompletion_IsNotRemoved()
        {
            // Defence in depth for the restart case: whichever path reaches the row first, a
            // completion the client reported must not be deleted as missing from the client.
            await HoldCompletionAsync();
            var unobserved = await TrackAsync("cccc3333");

            await OrphanCleanup.RemoveOrphansAsync(
                _client,
                CompleteSnapshot(),
                [],
                [(await HeldDownloadAsync())!, unobserved]);

            Assert.NotNull(await HeldDownloadAsync());

            // The control: a row in the same state with no observed completion is removed, so the
            // survivor above is the guard and not cleanup declining to run.
            Assert.Null(await _downloadRepository.GetByIdAsync(unobserved.Id));
        }

        private const string OverdueHoldLine = "completion hold has passed its window but the client poll was incomplete";

        private static string HeldId => $"tracked-{HeldHash}";

        private QbittorrentApiMock Api => _provider.GetRequiredService<QbittorrentApiMock>();

        private DownloadMonitorProcessor Processor => _provider.GetRequiredService<DownloadMonitorProcessor>();

        private DownloadOrphanCleanupService OrphanCleanup => _provider.GetRequiredService<DownloadOrphanCleanupService>();

        // A process restart, as far as the monitor can tell: the same database, fresh memory.
        private DownloadMonitorProcessor RestartedProcessor() =>
            ActivatorUtilities.CreateInstance<DownloadMonitorProcessor>(_provider);

        private Task<Download?> HeldDownloadAsync() => _downloadRepository.GetByIdAsync(HeldId);

        private async Task RunCycleAsync(DownloadMonitorProcessor processor)
        {
            processor.ScheduleNextClientPoll(_client, -100);
            await processor.RunCycleAsync(CancellationToken.None);
        }

        // The held torrent reports complete on a first cycle and is held, with another torrent
        // in progress beside it so that a snapshot without the held one is not empty.
        private async Task HoldCompletionAsync()
        {
            await TrackAsync(HeldHash);
            await TrackAsync(OtherHash);
            Api.InfoResponseOverride = Queue(
                Torrent(HeldHash, complete: true),
                Torrent(OtherHash, complete: false));

            await RunCycleAsync(Processor);

            Assert.Equal(DownloadStatus.Downloading, (await HeldDownloadAsync())!.Status);
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(HeldId));
        }

        // Old enough that orphan cleanup's grace period no longer protects it.
        private async Task<Download> TrackAsync(string hash)
        {
            var download = new DownloadBuilder()
                .WithId($"tracked-{hash}")
                .WithTitle(hash)
                .WithStatus(DownloadStatus.Downloading)
                .WithStartDate(DateTime.UtcNow.AddMinutes(-10))
                .WithExternalId(hash)
                .Build();
            download.DownloadClientId = _client.Id;
            return await _downloadRepository.AddAsync(download);
        }

        private ClientQueueFetchResult CompleteSnapshot() => new(
            _client,
            [new QueueItemBuilder().WithId(OtherHash).WithStatus("downloading").Build()],
            usedCachedSnapshot: false,
            isUnavailable: false,
            snapshotAge: TimeSpan.Zero,
            failureReason: null,
            snapshotState: "live",
            snapshotRefreshedAtUtc: DateTimeOffset.UtcNow);

        private static string Queue(params string[] torrents) => $"[{string.Join(",", torrents)}]";

        // Gateway-level reads reject a save path that is not valid on the host, so the fixtures
        // use host paths (JSON-encoded, as a Windows path carries backslashes).
        private static string Torrent(string hash, bool complete) => $$"""
            {
                "hash": "{{hash}}", "name": "{{hash}}", "progress": {{(complete ? "1" : "0.5")}}, "size": 1000,
                "downloaded": {{(complete ? 1000 : 500)}}, "state": "{{(complete ? "uploading" : "downloading")}}",
                "save_path": {{JsonSerializer.Serialize(FileUtils.GetAbsolutePath("downloads", hash))}}
            }
            """;

        private sealed class HoldLogRecorder : ILoggerProvider
        {
            private readonly object _gate = new();
            private readonly List<string> _messages = [];

            public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

            public int Count(string fragment)
            {
                lock (_gate)
                {
                    return _messages.Count(message => message.Contains(fragment, StringComparison.Ordinal));
                }
            }

            public void Dispose()
            {
            }

            private sealed class RecordingLogger(HoldLogRecorder owner) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(
                    LogLevel logLevel,
                    EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter)
                {
                    ArgumentNullException.ThrowIfNull(formatter);
                    lock (owner._gate)
                    {
                        owner._messages.Add(formatter(state, exception));
                    }
                }
            }
        }
    }
}
