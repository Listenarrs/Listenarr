using Microsoft.EntityFrameworkCore;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class MoveJobProcessorTests
{
    [Fact]
    public async Task ProcessJobAsync_HistoryHandoffFailsOnce_RetriesBeforeCompletion()
    {
        var source = FileService.GetTempDirectory("move-processor-history-handoff-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-history-handoff-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "History Handoff Retry",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var faultingContentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailCompletionHandoffOnce(CompletionHandoffFaultPoint.BeforeHistoryPersist));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingContentMoveService);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        var retryJob = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retryJob.Status);
        Assert.Equal(MoveJobPhase.RecordingCompletion, retryJob.Phase);
        Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"));
        await MakeRetryDueAsync(job.Id);
        var generation = Assert.IsType<int>(
            await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        retryJob.LeaseOwner = LeaseOwner;
        retryJob.LeaseGeneration = generation;

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(retryJob, CancellationToken.None);

        Assert.Equal(MoveJobStatus.Completed, (await queue.GetJobAsync(job.Id))?.Status);
        Assert.Single(
            await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"),
            entry => entry.EventType == "Moved");
    }

    [WindowsFact]
    public async Task ProcessJobAsync_TargetReplacementBlockedByCompletionLease_RetriesThenDetectsReplacement()
    {
        var source = FileService.GetTempDirectory("move-processor-history-target-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-history-target-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "History Target Replacement",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var contentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new ReplaceTargetBeforeCompletionHistory(target));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            contentMoveService);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        var retry = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retry.Status);
        Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"));
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            Assert.False(await db.MoveScanHandoffs.AsNoTracking()
                .AnyAsync(candidate => candidate.MoveJobId == job.Id));
        }
        var targetFile = Path.Join(target, "book.m4b");
        Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));

        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(targetFile);
        File.Delete(targetFile);
        await File.WriteAllTextAsync(targetFile, "audio");
        File.SetLastWriteTimeUtc(targetFile, lastWriteTimeUtc);
        await MakeRetryDueAsync(job.Id);
        var generation = Assert.IsType<int>(
            await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        retry.LeaseOwner = LeaseOwner;
        retry.LeaseGeneration = generation;

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(retry, CancellationToken.None);

        var blocked = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.NeedsAttention, blocked.Status);
        Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"));
        await using var verification = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        Assert.False(await verification.MoveScanHandoffs.AsNoTracking()
            .AnyAsync(candidate => candidate.MoveJobId == job.Id));
    }

    [LinuxFact]
    public async Task ProcessJobAsync_TargetContentMutatedInPlaceDuringCompletionCommit_WritesNoCompletionRecords()
    {
        var source = FileService.GetTempDirectory(
            "move-processor-completion-commit-content-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-completion-commit-content-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Completion Commit Content Mutation",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var contentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new MutateTargetContentDuringCompletionCommit(target));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            contentMoveService);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        var persisted = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.NeedsAttention, persisted.Status);
        Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        Assert.False(await db.MoveScanHandoffs.AsNoTracking()
            .AnyAsync(candidate => candidate.MoveJobId == job.Id));
        Assert.Equal(
            "evila",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [LinuxFact]
    public async Task ProcessJobAsync_TargetParentReplacedDuringCompletionCommit_WritesNoCompletionRecords()
    {
        var source = FileService.GetTempDirectory(
            "move-processor-completion-commit-parent-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-completion-commit-parent-dst-{Guid.NewGuid():N}");
        var displacedTarget = target + ".original";
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Completion Commit Parent Replacement",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var replacement = new ReplaceTargetParentDuringCompletionCommit(
            target,
            displacedTarget);
        var contentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            replacement);
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            contentMoveService);

        var processing = Task.Run(() =>
            processor.ProcessJobAsync(job, CancellationToken.None));
        await replacement.ReplacementInstalled;
        Assert.True(Directory.Exists(displacedTarget));
        Assert.Equal(
            "audio",
            await File.ReadAllTextAsync(Path.Join(displacedTarget, "book.m4b")));
        Assert.Equal(
            "foreign-target",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        replacement.Release();
        await processing;

        var persisted = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.NeedsAttention, persisted.Status);
        Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"));
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        Assert.False(await db.MoveScanHandoffs.AsNoTracking()
            .AnyAsync(candidate => candidate.MoveJobId == job.Id));
        Assert.True(Directory.Exists(target));
        Assert.Equal(
            "foreign-target",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_LeaseReplacedBeforeHistoryPersist_WritesNoCompletionHistory()
    {
        var source = FileService.GetTempDirectory("move-processor-history-lease-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-history-lease-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "History Lease Replacement",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var contentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            factory,
            TimeProvider.System,
            new ReplaceLeaseBeforeCompletionHistory(factory));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            contentMoveService);

        await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
            processor.ProcessJobAsync(job, CancellationToken.None));

        Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"));
        var persisted = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal("replacement-completion-worker", persisted.LeaseOwner);
        Assert.Equal(2, persisted.LeaseGeneration);
    }

    [Fact]
    public async Task ProcessJobAsync_PostCommitScanDispatchFailure_PreservesDurableHandoff()
    {
        var source = FileService.GetTempDirectory("move-processor-scan-lease-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-scan-lease-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Scan Lease Replacement",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var contentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailCompletionHandoffOnce(CompletionHandoffFaultPoint.BeforeScanEnqueue));
        var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            contentMoveService,
            scanQueue.Object);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        var correlated = await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}");
        Assert.Single(correlated, entry => entry.EventType == "Moved");
        scanQueue.VerifyNoOtherCalls();
        var persisted = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.Completed, persisted.Status);
        Assert.Null(persisted.LeaseOwner);
        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync();
        var handoff = await db.MoveScanHandoffs.AsNoTracking()
            .SingleAsync(candidate => candidate.MoveJobId == job.Id);
        Assert.Equal(MoveScanHandoffStatus.Pending, handoff.Status);
    }

    [Fact]
    public async Task RunPostCompletionEffectsAsync_MoveNotificationCancellation_StillAttemptsDurableScanHandoff()
    {
        var context = new MovePostCommitContext(
            Guid.NewGuid(),
            int.MaxValue,
            "Post Commit Notification Cancellation",
            Path.Join(FileService.GetTempPath(), "post-commit-source"),
            Path.Join(FileService.GetTempPath(), "post-commit-target"),
            Guid.NewGuid(),
            MoveHistoryId: 0,
            MoveHistoryCreated: false);
        var moveQueue = new Mock<IMoveQueueService>(MockBehavior.Strict);
        moveQueue.Setup(service => service.NotifyPersistedJobStateAsync(
                context.JobId,
                MoveJobStatus.Completed,
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException(
                "Injected completed-move notification cancellation."));
        var handoffStore = new Mock<IMoveScanHandoffStore>(MockBehavior.Strict);
        handoffStore.Setup(store => store.TryClaimAsync(
                context.HandoffId,
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((MoveScanHandoffClaim?)null);
        var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            moveQueue.Object,
            handoffStore.Object,
            scanQueue.Object);

        await processor.RunPostCompletionEffectsAsync(
            context,
            CancellationToken.None);

        moveQueue.Verify(service => service.NotifyPersistedJobStateAsync(
            context.JobId,
            MoveJobStatus.Completed,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
        handoffStore.Verify(store => store.TryClaimAsync(
            context.HandoffId,
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        scanQueue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessJobAsync_DurableScanFailure_DoesNotReplayHandoff()
    {
        var state = await CreateMarkerlessFinalizedCopyStateAsync();
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            db.MoveScanHandoffs.Add(new MoveScanHandoff
            {
                MoveJobId = state.Job.Id,
                AudiobookId = state.Job.AudiobookId,
                TargetPath = state.Job.RequestedPath!,
                Status = MoveScanHandoffStatus.Failed,
                LastError = "Prior durable scan failure"
            });
            await db.SaveChangesAsync();
        }
        var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            scanQueue.Object);

        await processor.ProcessJobAsync(state.Job, CancellationToken.None);

        Assert.Equal(MoveJobStatus.Completed, (await state.Queue.GetJobAsync(state.Job.Id))?.Status);
        scanQueue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessJobAsync_ImmediateScanDispatchFails_CompletesAndOutboxRecovers()
    {
        var source = FileService.GetTempDirectory("move-processor-scan-handoff-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-scan-handoff-dst-{Guid.NewGuid():N}");
        await AddAuthorizedRootAsync(FileService.GetTempPath());
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Scan Handoff Retry",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var faultingContentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailCompletionHandoffOnce(CompletionHandoffFaultPoint.BeforeScanEnqueue));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingContentMoveService);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        Assert.Equal(MoveJobStatus.Completed, (await queue.GetJobAsync(job.Id))?.Status);
        var correlatedHistory = await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}");
        Assert.Single(correlatedHistory, entry => entry.EventType == "Moved");
        await using (var db = await _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            var handoff = await db.MoveScanHandoffs.AsNoTracking()
                .SingleAsync(candidate => candidate.MoveJobId == job.Id);
            Assert.Equal(MoveScanHandoffStatus.Pending, handoff.Status);
        }

        var scanQueue = Assert.IsType<ScanQueueService>(
            _provider.GetRequiredService<IScanQueueService>());
        Assert.False(scanQueue.Reader.TryRead(out _));
        await ActivatorUtilities.CreateInstance<MoveScanHandoffRecoveryService>(_provider)
            .RecoverAsync(CancellationToken.None);
        Assert.True(scanQueue.Reader.TryRead(out var recoveredScan));
        Assert.Equal($"move:{job.Id:N}", recoveredScan.CorrelationId);
        Assert.NotNull(recoveredScan.MoveScanHandoffId);
        Assert.True(recoveredScan.PhysicalIdentity.HasValue);
    }

    private sealed class ReplaceTargetBeforeCompletionHistory(string target)
        : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnCompletionHandoff(
            Guid jobId,
            CompletionHandoffFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != CompletionHandoffFaultPoint.BeforeHistoryPersist)
            {
                return;
            }

            var file = Path.Join(target, "book.m4b");
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(file);
            var content = File.ReadAllBytes(file);
            File.Delete(file);
            File.WriteAllBytes(file, content);
            File.SetLastWriteTimeUtc(file, lastWriteTimeUtc);
            _replaced = true;
        }
    }

    private sealed class MutateTargetContentDuringCompletionCommit(string target)
        : IMoveFaultInjector
    {
        private bool _mutated;

        public void OnCompletionHandoff(
            Guid jobId,
            CompletionHandoffFaultPoint faultPoint)
        {
            if (_mutated
                || faultPoint
                    != CompletionHandoffFaultPoint.BeforeCompletionCommitValidation)
            {
                return;
            }

            var targetFile = Path.Join(target, "book.m4b");
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(targetFile);
            File.WriteAllText(targetFile, "evila");
            File.SetLastWriteTimeUtc(targetFile, lastWriteTimeUtc);
            _mutated = true;
        }
    }

    private sealed class ReplaceTargetParentDuringCompletionCommit(
        string target,
        string displacedTarget) : IMoveFaultInjector
    {
        private readonly TaskCompletionSource _replacementInstalled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _replaced;

        public Task ReplacementInstalled => _replacementInstalled.Task;

        public void Release() => _release.TrySetResult();

        public void OnCompletionHandoff(
            Guid jobId,
            CompletionHandoffFaultPoint faultPoint)
        {
            if (_replaced
                || faultPoint
                    != CompletionHandoffFaultPoint.BeforeCompletionCommitValidation)
            {
                return;
            }

            Directory.Move(target, displacedTarget);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Join(target, "book.m4b"), "foreign-target");
            _replaced = true;
            _replacementInstalled.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class ReplaceLeaseBeforeCompletionHistory(
        IDbContextFactory<ListenArrDbContext> factory) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnCompletionHandoff(
            Guid jobId,
            CompletionHandoffFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != CompletionHandoffFaultPoint.BeforeHistoryPersist)
            {
                return;
            }

            using var db = factory.CreateDbContext();
            var job = db.MoveJobs.Single(candidate => candidate.Id == jobId);
            job.LeaseOwner = "replacement-completion-worker";
            job.LeaseGeneration++;
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
            db.SaveChanges();
            _replaced = true;
        }
    }

    private sealed class ReplaceLeaseBeforeScanEnqueue(
        IDbContextFactory<ListenArrDbContext> factory) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnCompletionHandoff(
            Guid jobId,
            CompletionHandoffFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != CompletionHandoffFaultPoint.BeforeScanEnqueue)
            {
                return;
            }

            using var db = factory.CreateDbContext();
            var job = db.MoveJobs.Single(candidate => candidate.Id == jobId);
            job.LeaseOwner = "replacement-scan-worker";
            job.LeaseGeneration++;
            job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5);
            db.SaveChanges();
            _replaced = true;
        }
    }

    private sealed class FailCompletionHandoffOnce(
        CompletionHandoffFaultPoint expectedPoint) : IMoveFaultInjector
    {
        private bool _failed;

        public void OnCompletionHandoff(
            Guid jobId,
            CompletionHandoffFaultPoint faultPoint)
        {
            if (_failed || faultPoint != expectedPoint)
            {
                return;
            }

            _failed = true;
            throw new IOException($"Simulated completion handoff failure at {faultPoint}.");
        }
    }
}
