/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Collections.Concurrent;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;
using Xunit.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Jobs;

/// <summary>
/// Drives the real rescan processor through whole cycles on SQLite with a manual
/// clock and a scripted extractor, and reads the persisted schedule back.
/// </summary>
[Trait("Area", "Metadata")]
[Trait("Name", "MetadataRescanSchedulingTests")]
[Trait("Category", "Infrastructure")]
public sealed partial class MetadataRescanSchedulingTests : BaseTests
{
    private readonly ITestOutputHelper _output;

    public MetadataRescanSchedulingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task RunCycleAsync_FixedBacklogOfFailingFiles_ReachesTheHealthyFileWithinElevenCycles()
    {
        await InitSqliteAsync();
        var failing = await SeedBookAsync("failing", 200);
        var healthy = await SeedBookAsync("healthy", 1);
        _extract = FailFor(failing.Paths);
        var processor = NewProcessor();

        var cycle = await CyclesUntilExtractedAsync(() => processor, healthy.Paths[0], maxCycles: 11);

        Assert.True(cycle.HasValue, "the healthy file was not extracted within 11 cycles");
        Assert.Equal(90, (await ReadFileAsync(healthy.Files[0].Id)).DurationSeconds);
    }

    [Fact]
    public async Task RunCycleAsync_FixedBacklogWithARestartEveryCycle_ReachesTheHealthyFileWithinElevenCycles()
    {
        await InitSqliteAsync();
        var failing = await SeedBookAsync("failing", 200);
        var healthy = await SeedBookAsync("healthy", 1);
        _extract = FailFor(failing.Paths);

        var cycle = await CyclesUntilExtractedAsync(() => NewProcessor(), healthy.Paths[0], maxCycles: 11);

        Assert.True(cycle.HasValue, "the healthy file was not extracted within 11 cycles");
    }

    [Fact]
    public async Task RunCycleAsync_NewFilesArrivingEveryCycle_DoNotDelayARetryPastItsDueTime()
    {
        await InitSqliteAsync();
        var retry = await SeedBookAsync("retry", 1, NowUtc.AddMinutes(-1));
        var retrySucceeds = false;
        _extract = path => Task.FromResult(path == retry.Paths[0] && !retrySucceeds ? null : CompleteMetadata());
        var processor = NewProcessor();
        DateTime? retryDueUtc = null;
        int? firstDueCycle = null;
        int? retriedCycle = null;

        for (var cycle = 1; cycle <= 8; cycle++)
        {
            await SeedBookAsync($"arrivals-{cycle}", 20, NowUtc);
            var cycleNowUtc = NowUtc;
            var extracted = await RunCycleAsync(processor);
            _output.WriteLine($"cycle {cycle} at +{(cycleNowUtc - _clock.Start).TotalMinutes} min: extracted {extracted.Count}, retry extracted {extracted.Contains(retry.Paths[0])}");
            if (cycle == 1)
            {
                retryDueUtc = (await ReadFileAsync(retry.Files[0].Id)).MetadataRescanDueUtc;
                Assert.Equal(cycleNowUtc + MetadataRescanSchedulePolicy.FailureBaseDelay, retryDueUtc);
                retrySucceeds = true;
                continue;
            }

            if (cycleNowUtc >= retryDueUtc)
            {
                firstDueCycle ??= cycle;
            }

            if (extracted.Contains(retry.Paths[0]))
            {
                retriedCycle ??= cycle;
            }
        }

        Assert.NotNull(firstDueCycle);
        Assert.True(
            retriedCycle == firstDueCycle,
            $"the retry due at {retryDueUtc:O} was extracted in cycle {retriedCycle?.ToString() ?? "none"}, not in cycle {firstDueCycle}, the first cycle at or after its due time");
    }

    [Fact]
    public async Task RunCycleAsync_RestartBetweenCycles_KeepsTheStoredDueTimesAndTheNextSelection()
    {
        var continuous = await RunTwoCyclesAsync(restartBetween: false);
        var restarted = await RunTwoCyclesAsync(restartBetween: true);

        Assert.Equal(continuous.SecondCycle, restarted.SecondCycle);
        Assert.Equal(continuous.StoredDueOffsets, restarted.StoredDueOffsets);
        Assert.All(continuous.FirstCycle, name => Assert.Equal(
            MetadataRescanSchedulePolicy.FailureBaseDelay,
            continuous.StoredDueOffsets[name]));
    }

    [Fact]
    public async Task RunCycleAsync_CancelledMidCycle_KeepsEarlierOutcomesAndCountsNoFailure()
    {
        await InitSqliteAsync();
        var book = await SeedBookAsync("cancel", 3);
        using var cycleCancellation = new CancellationTokenSource();
        var roles = new ConcurrentDictionary<string, int>();
        // The first file to reach the extractor yields no metadata and is stored; the second
        // stops the cycle once that is stored; any later one waits for the stop. Both of those
        // end cancelled.
        _extract = async path =>
        {
            var role = roles.GetOrAdd(path, _ => roles.Count + 1);
            if (role == 1)
            {
                return null;
            }

            if (role == 2)
            {
                await WaitForAsync(async () => await StoredFailuresAsync(book) == 1);
                await cycleCancellation.CancelAsync();
            }
            else
            {
                await WaitForAsync(() => Task.FromResult(cycleCancellation.IsCancellationRequested));
            }

            throw new OperationCanceledException(cycleCancellation.Token);
        };
        var dueBefore = new Dictionary<int, DateTime>();
        foreach (var (id, _) in book.Files)
        {
            dueBefore[id] = (await ReadFileAsync(id)).MetadataRescanDueUtc;
        }

        await RunCycleAsync(NewProcessor(), cycleCancellation.Token);

        var stored = new List<AudiobookFile>();
        foreach (var (id, _) in book.Files)
        {
            stored.Add(await ReadFileAsync(id));
        }

        Assert.True(roles.Count >= 2, $"only {roles.Count} file reached the extractor before the cycle stopped");
        var failed = Assert.Single(stored, file => file.MetadataRescanFailureCount != 0);
        Assert.Equal(1, failed.MetadataRescanFailureCount);
        Assert.Equal(nameof(MetadataRescanOutcome.NoMetadata), failed.MetadataRescanLastOutcome);
        foreach (var cancelled in stored.Where(file => file.Id != failed.Id))
        {
            Assert.Null(cancelled.MetadataRescanLastOutcome);
            Assert.Equal(dueBefore[cancelled.Id], cancelled.MetadataRescanDueUtc);
        }
    }

    [Fact]
    public async Task RunCycleAsync_TimeoutWhileTheCycleRuns_IsAFailedExtraction()
    {
        await InitSqliteAsync();
        var book = await SeedBookAsync("timeout", 1);
        _extract = _ => throw new TaskCanceledException("The extraction timed out.");
        var cycleNowUtc = NowUtc;

        await RunCycleAsync(NewProcessor());

        var file = await ReadFileAsync(book.Files[0].Id);
        Assert.Equal(nameof(MetadataRescanOutcome.ExtractionFailed), file.MetadataRescanLastOutcome);
        Assert.Equal(1, file.MetadataRescanFailureCount);
        Assert.Equal(cycleNowUtc + MetadataRescanSchedulePolicy.FailureBaseDelay, file.MetadataRescanDueUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunCycleAsync_MoveRecoveryBlockInsideTheTask_LeavesTheFailureCountAtZero(bool conflictDuringRefresh)
    {
        // Either the refresh's own mutation check refuses, or the task's recovery check (which
        // runs after selection) finds the block.
        var refusing = RefusingMoveQueue(new ApplicationConflictException("move_recovery_pending", "A move must be recovered first."));
        await InitSqliteAsync(builder =>
        {
            if (conflictDuringRefresh)
            {
                builder.WithSingleton(refusing);
            }
        });
        var book = await SeedBookAsync("blocked-in-task", 1);
        var taskMoveQueue = new Mock<IMoveQueueService>();
        taskMoveQueue.Setup(service => service.GetRecoveryStateForAudiobookAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MoveRecoveryState.None with { Disposition = MoveRecoveryDisposition.InProgress });
        var cycleNowUtc = NowUtc;

        await RunCycleAsync(conflictDuringRefresh ? NewProcessor() : NewProcessor(taskMoveQueue.Object));

        var file = await ReadFileAsync(book.Files[0].Id);
        Assert.Equal(nameof(MetadataRescanOutcome.MoveRecoveryBlocked), file.MetadataRescanLastOutcome);
        Assert.Equal(0, file.MetadataRescanFailureCount);
        Assert.Equal(1, file.MetadataRescanBlockedCount);
        Assert.Equal(cycleNowUtc + MetadataRescanSchedulePolicy.BlockedBaseDelay, file.MetadataRescanDueUtc);
    }

    [Fact]
    public async Task RunCycleAsync_PersistentBlock_RetriesOnTheBlockedLadderWithoutFailures()
    {
        await InitSqliteAsync();
        var book = await SeedBookAsync("persistently-blocked", 1);
        var taskMoveQueue = new Mock<IMoveQueueService>();
        taskMoveQueue.Setup(service => service.GetRecoveryStateForAudiobookAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MoveRecoveryState.None with { Disposition = MoveRecoveryDisposition.InProgress });
        var processor = NewProcessor(taskMoveQueue.Object);
        var attemptMinutes = new List<double>();
        var blockedCount = 0;

        for (var cycle = 0; cycle < 60; cycle++)
        {
            var cycleNowUtc = NowUtc;
            await RunCycleAsync(processor);
            var file = await ReadFileAsync(book.Files[0].Id);
            Assert.Equal(0, file.MetadataRescanFailureCount);
            if (file.MetadataRescanBlockedCount != blockedCount)
            {
                blockedCount = file.MetadataRescanBlockedCount;
                attemptMinutes.Add((cycleNowUtc - _clock.Start).TotalMinutes);
            }
        }

        var gaps = attemptMinutes.Zip(attemptMinutes.Skip(1), (earlier, later) => later - earlier).ToList();
        _output.WriteLine($"attempts at +[{string.Join(", ", attemptMinutes)}] min, gaps [{string.Join(", ", gaps)}]");
        Assert.Equal(new double[] { 10, 20, 40, 80, 120 }, gaps);
    }

    [Fact]
    public async Task RunCycleAsync_ManyBlockedFiles_DoNotStopAHealthyFile()
    {
        await InitSqliteAsync();
        var blocked = await SeedBookAsync("blocked", 60);
        var healthy = await SeedBookAsync("healthy", 1);
        await MoveJobTestFactory.SeedUnresolvedExecutionAsync(
            _store,
            blocked.AudiobookId,
            blocked.BasePath,
            Path.Join(FileService.GetTempPath(), $"rescan-move-target-{Guid.NewGuid():N}"));

        var cycle = await CyclesUntilExtractedAsync(() => NewProcessor(), healthy.Paths[0], maxCycles: 2);

        Assert.True(cycle.HasValue, "the healthy file was not extracted within 2 cycles");
        foreach (var (id, _) in blocked.Files)
        {
            var file = await ReadFileAsync(id);
            Assert.Equal(nameof(MetadataRescanOutcome.MoveRecoveryBlocked), file.MetadataRescanLastOutcome);
            Assert.Equal(0, file.MetadataRescanFailureCount);
        }
    }

    [Fact]
    public async Task RunCycleAsync_FileGoneBeforeItsLeaseOpens_RecordsFileMissing()
    {
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        await InitSqliteAsync(builder => builder.WithSingleton(fileSystem.Object));
        var book = await SeedBookAsync("vanished", 1);
        File.Delete(book.Paths[0]);

        await RunCycleAsync(NewProcessor());

        var file = await ReadFileAsync(book.Files[0].Id);
        Assert.Equal(nameof(MetadataRescanOutcome.FileMissing), file.MetadataRescanLastOutcome);
        Assert.Equal(1, file.MetadataRescanUnavailableCount);
        Assert.Equal(0, file.MetadataRescanFailureCount);
        Assert.Empty(_cycleExtractions);
    }

    [Fact]
    public async Task RunCycleAsync_RepeatedFailureAfterARestart_LogsOnlyTheFirstAtWarning()
    {
        await InitSqliteAsync(builder => builder.WithSingleton(
            RefusingMoveQueue(new InvalidOperationException("The guarded flow fails."))));
        var book = await SeedBookAsync("logging", 1);
        var logger = new CapturingLogger();

        await RunCycleAsync(NewProcessor(logger: logger));
        var firstLevels = logger.LevelsContaining("Failed to rescan metadata");
        logger.Clear();
        _clock.Advance(MetadataRescanSchedulePolicy.FailureBaseDelay);
        await RunCycleAsync(NewProcessor(logger: logger));
        var secondLevels = logger.LevelsContaining("Failed to rescan metadata");

        Assert.Equal(new[] { LogLevel.Warning }, firstLevels);
        Assert.Equal(new[] { LogLevel.Debug }, secondLevels);
        Assert.Equal(2, (await ReadFileAsync(book.Files[0].Id)).MetadataRescanFailureCount);
    }

    // These files never yield metadata (NoMetadata, a failure-ladder outcome).
    private static Func<string, Task<AudioMetadata?>> FailFor(IEnumerable<string> paths)
    {
        var failing = paths.ToHashSet(StringComparer.Ordinal);
        return path => Task.FromResult(failing.Contains(path) ? null : CompleteMetadata());
    }

    // A move queue that reports no recovery state, so selection passes, and then refuses the
    // refresh's own filesystem-mutation check with the given exception.
    private static IMoveQueueService RefusingMoveQueue(Exception refusal)
    {
        var moveQueue = new Mock<IMoveQueueService>();
        moveQueue.Setup(service => service.GetRecoveryStateForAudiobookAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MoveRecoveryState.None);
        moveQueue.Setup(service => service.EnsureFilesystemMutationAllowedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(refusal);
        return moveQueue.Object;
    }

    // The 1-based cycle in which the file was extracted, or null if it never was.
    private async Task<int?> CyclesUntilExtractedAsync(
        Func<MetadataRescanProcessor> processorForCycle,
        string path,
        int maxCycles)
    {
        for (var cycle = 1; cycle <= maxCycles; cycle++)
        {
            var extracted = await RunCycleAsync(processorForCycle());
            _output.WriteLine($"cycle {cycle}: extracted {extracted.Count}, target extracted {extracted.Contains(path)}");
            if (extracted.Contains(path))
            {
                return cycle;
            }
        }

        return null;
    }

    // 60 always-failing files; two cycles on one processor, or with a new processor between
    // them. Names are file names, so the two independent stores compare directly.
    private async Task<TwoCycleRun> RunTwoCyclesAsync(bool restartBetween)
    {
        await InitSqliteAsync();
        _clock.Reset();
        var book = await SeedBookAsync(restartBetween ? "restarted" : "continuous", 60);
        _extract = FailFor(book.Paths);
        var processor = NewProcessor();

        var firstCycleStartUtc = NowUtc;
        var firstCycle = Names(await RunCycleAsync(processor));
        var secondCycle = Names(await RunCycleAsync(restartBetween ? NewProcessor() : processor));

        var storedDueOffsets = new Dictionary<string, TimeSpan>();
        foreach (var (id, path) in book.Files.Where(file => firstCycle.Contains(Path.GetFileName(file.Path))))
        {
            storedDueOffsets[Path.GetFileName(path)] = (await ReadFileAsync(id)).MetadataRescanDueUtc - firstCycleStartUtc;
        }

        _output.WriteLine($"restart {restartBetween}: cycle 1 [{string.Join(", ", firstCycle)}], cycle 2 [{string.Join(", ", secondCycle)}]");
        return new TwoCycleRun(firstCycle, secondCycle, storedDueOffsets);

        static List<string> Names(IEnumerable<string> paths) => paths.Select(path => Path.GetFileName(path)).Order().ToList();
    }

    private async Task<int> StoredFailuresAsync(SeededBook book)
    {
        var failures = 0;
        foreach (var (id, _) in book.Files)
        {
            failures += (await ReadFileAsync(id)).MetadataRescanFailureCount;
        }

        return failures;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the awaited condition did not hold within 30 seconds");
            await Task.Delay(20);
        }
    }

    private sealed record TwoCycleRun(
        IReadOnlyList<string> FirstCycle,
        IReadOnlyList<string> SecondCycle,
        IReadOnlyDictionary<string, TimeSpan> StoredDueOffsets);
}
