using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class MoveJobProcessorTests
{
    [Fact]
    public async Task ProcessJobAsync_ArtifactCleanupFailsOnce_SchedulesAndCompletesRetry()
    {
        var source = FileService.GetTempDirectory("move-processor-artifact-retry-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-artifact-retry-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Artifact Cleanup Retry",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var faultingContentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailCompletedArtifactCleanupOnce());
        var faultingProcessor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingContentMoveService);

        await faultingProcessor.ProcessJobAsync(job, CancellationToken.None);

        var retryJob = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retryJob.Status);
        Assert.Equal(MoveJobPhase.CleaningArtifacts, retryJob.Phase);
        var markerPath = Path.Join(target, $".listenarr-move-{job.Id:N}.pending");
        Assert.True(File.Exists(markerPath));
        Assert.Empty(await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"));
        Assert.NotNull(retryJob.NextAttemptAt);
        Assert.Null(await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        await MakeRetryDueAsync(job.Id);

        var retryGeneration = Assert.IsType<int>(
            await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        retryJob.LeaseOwner = LeaseOwner;
        retryJob.LeaseGeneration = retryGeneration;
        var retryProcessor = _provider.GetRequiredService<IMoveJobProcessor>();

        await retryProcessor.ProcessJobAsync(retryJob, CancellationToken.None);

        Assert.Equal(MoveJobStatus.Completed, (await queue.GetJobAsync(job.Id))?.Status);
        Assert.False(File.Exists(markerPath));
        Assert.Single(
            await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"),
            entry => entry.EventType == "Moved");
    }

    [Fact]
    public async Task ProcessJobAsync_EmptySourceStateNativeDeleteFailure_SchedulesAndCompletesRetry()
    {
        var source = FileService.GetTempDirectory("move-processor-source-state-delete-retry-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-source-state-delete-retry-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Source State Delete Retry",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var faultingContentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailEmptySourceStateDeleteOnce());
        var faultingProcessor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingContentMoveService);

        await faultingProcessor.ProcessJobAsync(job, CancellationToken.None);

        var retryJob = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retryJob.Status);
        Assert.NotNull(retryJob.NextAttemptAt);
        Assert.False(Directory.Exists(source));
        var statePath = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{job.Id:N}",
            ".listenarr-empty-source.state");
        Assert.True(Directory.Exists(statePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(statePath));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        await MakeRetryDueAsync(job.Id);

        var retryGeneration = Assert.IsType<int>(
            await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        retryJob.LeaseOwner = LeaseOwner;
        retryJob.LeaseGeneration = retryGeneration;
        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(retryJob, CancellationToken.None);

        var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.Completed, completed.Status);
        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(statePath));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_ForeignSourceFileBeforeMarkerDelete_PreservesFileAndCompletes()
    {
        var source = FileService.GetTempDirectory("move-processor-recreated-source-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-recreated-source-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Recreated Source",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var faultingContentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new RecreateSourceBeforeMarkerDelete(source));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingContentMoveService);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        var persisted = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.Completed, persisted.Status);
        Assert.Equal(
            "preserve me",
            await File.ReadAllTextAsync(Path.Join(source, "operator-note.txt")));
        Assert.False(File.Exists(Path.Join(
            target,
            $".listenarr-move-{job.Id:N}.pending")));
    }

    [Fact]
    public async Task ProcessJobAsync_TargetChangesBeforeMarkerDelete_RequiresAttentionAndPreservesMarker()
    {
        var source = FileService.GetTempDirectory("move-processor-mutated-target-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-mutated-target-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Mutated Target",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var faultingContentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new MutateTargetBeforeMarkerDelete(target));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingContentMoveService);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        var persisted = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.NeedsAttention, persisted.Status);
        Assert.Equal(
            "corrupted after cleanup validation",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        Assert.True(File.Exists(Path.Join(
            target,
            $".listenarr-move-{job.Id:N}.pending")));
    }

    [Fact]
    public async Task ProcessJobAsync_UnownedFileAppearsAfterFinalHash_PreservesMarkerAndRequiresAttention()
    {
        var source = FileService.GetTempDirectory("move-processor-final-hash-race-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-final-hash-race-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Final Hash Ownership Race",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var contentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new AddUnownedFileAfterFinalHash(target));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            contentMoveService);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        var persisted = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.NeedsAttention, persisted.Status);
        Assert.Equal(
            "preserve me",
            await File.ReadAllTextAsync(Path.Join(target, "operator-note.txt")));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.True(File.Exists(Path.Join(
            target,
            $".listenarr-move-{job.Id:N}.pending")));
    }

    [Fact]
    public async Task ProcessJobAsync_FinalizationIoFailure_SchedulesAndCompletesRetry()
    {
        var sourceRoot = FileService.GetTempDirectory("move-processor-finalize-retry-root");
        var sourceParent = Path.Join(sourceRoot, "Author", "Old Title");
        var source = Path.Join(sourceParent, "test");
        Directory.CreateDirectory(source);
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        await RecordOwnedDirectoryHierarchyAsync(sourceRoot, sourceParent);
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-finalize-retry-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Finalization Retry",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var faultingContentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailMoveFinalizationOnce());
        var faultingProcessor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingContentMoveService);

        await faultingProcessor.ProcessJobAsync(job, CancellationToken.None);

        var retryJob = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retryJob.Status);
        Assert.Equal(MoveJobPhase.Finalizing, retryJob.Phase);
        Assert.True(Directory.Exists(sourceParent));
        Assert.True(File.Exists(Path.Join(target, $".listenarr-move-{job.Id:N}.pending")));
        Assert.NotNull(retryJob.NextAttemptAt);
        Assert.Null(await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        await MakeRetryDueAsync(job.Id);

        var retryGeneration = Assert.IsType<int>(
            await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        retryJob.LeaseOwner = LeaseOwner;
        retryJob.LeaseGeneration = retryGeneration;
        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(retryJob, CancellationToken.None);

        Assert.Equal(MoveJobStatus.Completed, (await queue.GetJobAsync(job.Id))?.Status);
        Assert.False(Directory.Exists(sourceParent));
        Assert.True(Directory.Exists(sourceRoot));
        Assert.False(File.Exists(Path.Join(target, $".listenarr-move-{job.Id:N}.pending")));
    }

    [Fact]
    public async Task ProcessJobAsync_SourceAncestorReceivesContentDuringFinalization_PreservesItAndCompletes()
    {
        var sourceRoot = FileService.GetTempDirectory("move-processor-finalize-arrival-root");
        var sourceParent = Path.Join(sourceRoot, "Author", "Old Title");
        var source = Path.Join(sourceParent, "test");
        Directory.CreateDirectory(source);
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        await RecordOwnedDirectoryHierarchyAsync(sourceRoot, sourceParent);
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-finalize-arrival-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Finalization Arrival",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var contentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new AddFileBeforeSourceAncestorDelete(sourceParent));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            contentMoveService);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        var persisted = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.Completed, persisted.Status);
        Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(sourceParent));
        Assert.Equal(
            "preserve me",
            await File.ReadAllTextAsync(Path.Join(sourceParent, "operator-note.txt")));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_PersistentFinalizationFailure_StopsAtRetryLimit()
    {
        var sourceRoot = FileService.GetTempDirectory("move-processor-retry-limit-root");
        var source = Path.Join(sourceRoot, "Author", "Title", "test");
        Directory.CreateDirectory(source);
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        await RecordOwnedDirectoryHierarchyAsync(
            sourceRoot,
            Path.GetDirectoryName(source)!);
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-retry-limit-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Retry Limit",
            BasePath = source
        });
        var (queue, initialJob) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var contentMoveService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new AlwaysFailMoveFinalization());
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            contentMoveService);
        var currentJob = initialJob;
        var retryDelays = new List<TimeSpan>();

        for (var attempt = 1; attempt <= MoveTimingPolicy.MaxTransientAttempts; attempt++)
        {
            await processor.ProcessJobAsync(currentJob, CancellationToken.None);
            var persisted = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(initialJob.Id));
            Assert.Equal(attempt, persisted.AttemptCount);
            if (attempt == MoveTimingPolicy.MaxTransientAttempts)
            {
                Assert.Equal(MoveJobStatus.NeedsAttention, persisted.Status);
                Assert.Null(persisted.NextAttemptAt);
                Assert.Null(persisted.LeaseOwner);
                Assert.Null(persisted.LeaseExpiresAt);
                Assert.Contains("retry limit exhausted", persisted.Error, StringComparison.OrdinalIgnoreCase);
                break;
            }

            Assert.Equal(MoveJobStatus.RetryScheduled, persisted.Status);
            var nextAttemptAt = Assert.IsType<DateTime>(persisted.NextAttemptAt);
            var updatedAt = Assert.IsType<DateTime>(persisted.UpdatedAt);
            retryDelays.Add(nextAttemptAt - updatedAt);
            await MakeRetryDueAsync(initialJob.Id);
            var generation = Assert.IsType<int>(
                await queue.TryClaimJobAsync(initialJob.Id, LeaseOwner));
            persisted.LeaseOwner = LeaseOwner;
            persisted.LeaseGeneration = generation;
            currentJob = persisted;
        }

        Assert.Equal(
            retryDelays.OrderBy(delay => delay).ToList(),
            retryDelays);
        Assert.All(retryDelays, delay =>
            Assert.True(delay <= MoveTimingPolicy.MaxRetryDelay));
        Assert.True(File.Exists(Path.Join(
            target,
            $".listenarr-move-{initialJob.Id:N}.pending")));
    }

    private async Task MakeRetryDueAsync(Guid jobId)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var job = await db.MoveJobs.SingleAsync(candidate => candidate.Id == jobId);
        job.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }

    private sealed class FailEmptySourceStateDeleteOnce : IMoveFaultInjector
    {
        private bool _failed;

        public void OnSourceCleanupMutation(
            Guid jobId,
            SourceCleanupFaultPoint faultPoint)
        {
            if (_failed
                || faultPoint != SourceCleanupFaultPoint.BeforeEmptySourceStateDelete)
            {
                return;
            }

            _failed = true;
            throw new System.ComponentModel.Win32Exception(
                145,
                "Injected empty-source state retirement failure.");
        }
    }

    private sealed class RecreateSourceBeforeMarkerDelete(
        string source) : IMoveFaultInjector
    {
        private bool _recreated;

        public void OnCompletedArtifactCleanup(
            Guid jobId,
            CompletedArtifactCleanupFaultPoint faultPoint)
        {
            if (_recreated
                || faultPoint != CompletedArtifactCleanupFaultPoint.BeforeRecoveryMarkerDelete)
            {
                return;
            }

            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Join(source, "operator-note.txt"), "preserve me");
            _recreated = true;
        }
    }

    private sealed class MutateTargetBeforeMarkerDelete(
        string target) : IMoveFaultInjector
    {
        private bool _mutated;

        public void OnCompletedArtifactCleanup(
            Guid jobId,
            CompletedArtifactCleanupFaultPoint faultPoint)
        {
            if (_mutated
                || faultPoint != CompletedArtifactCleanupFaultPoint.BeforeRecoveryMarkerDelete)
            {
                return;
            }

            File.WriteAllText(
                Path.Join(target, "book.m4b"),
                "corrupted after cleanup validation");
            _mutated = true;
        }
    }

    private sealed class AddUnownedFileAfterFinalHash(
        string target) : IMoveFaultInjector
    {
        private bool _added;

        public void OnCompletedArtifactCleanup(
            Guid jobId,
            CompletedArtifactCleanupFaultPoint faultPoint)
        {
            if (_added
                || faultPoint != CompletedArtifactCleanupFaultPoint.BeforeFinalDestinationOwnershipValidation)
            {
                return;
            }

            File.WriteAllText(Path.Join(target, "operator-note.txt"), "preserve me");
            _added = true;
        }
    }

    private sealed class FailCompletedArtifactCleanupOnce : IMoveFaultInjector
    {
        private bool _failed;

        public void OnCompletedArtifactCleanup(
            Guid jobId,
            CompletedArtifactCleanupFaultPoint faultPoint)
        {
            if (_failed
                || faultPoint != CompletedArtifactCleanupFaultPoint.BeforeRecoveryMarkerDelete)
            {
                return;
            }

            _failed = true;
            throw new IOException("Simulated transient recovery marker lock.");
        }
    }

    private sealed class AddFileBeforeSourceAncestorDelete(
        string sourceParent) : IMoveFaultInjector
    {
        private bool _added;

        public void OnMoveFinalization(
            Guid jobId,
            MoveFinalizationFaultPoint faultPoint)
        {
            if (_added
                || faultPoint != MoveFinalizationFaultPoint.BeforeSourceAncestorDelete)
            {
                return;
            }

            File.WriteAllText(Path.Join(sourceParent, "operator-note.txt"), "preserve me");
            _added = true;
        }
    }

    private sealed class AlwaysFailMoveFinalization : IMoveFaultInjector
    {
        public void OnMoveFinalization(
            Guid jobId,
            MoveFinalizationFaultPoint faultPoint)
        {
            if (faultPoint == MoveFinalizationFaultPoint.BeforeSourceAncestorDelete)
            {
                throw new IOException("Simulated persistent source ancestor lock.");
            }
        }
    }

    private sealed class FailMoveFinalizationOnce : IMoveFaultInjector
    {
        private bool _failed;

        public void OnMoveFinalization(
            Guid jobId,
            MoveFinalizationFaultPoint faultPoint)
        {
            if (_failed
                || faultPoint != MoveFinalizationFaultPoint.BeforeSourceAncestorDelete)
            {
                return;
            }

            _failed = true;
            throw new IOException("Simulated transient source ancestor lock.");
        }
    }
}
