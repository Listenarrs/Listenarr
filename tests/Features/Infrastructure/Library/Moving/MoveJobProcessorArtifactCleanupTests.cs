using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class MoveJobProcessorTests
{
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
    }

    [Fact]
    public async Task ProcessJobAsync_SourceAncestorReceivesContentDuringFinalization_PreservesItAndCompletes()
    {
        var sourceRoot = FileService.GetTempDirectory("move-processor-finalize-arrival-root");
        await AddAuthorizedRootAsync(sourceRoot, "Finalization Arrival Source Root");
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
    }

    private async Task MakeRetryDueAsync(Guid jobId)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var job = await db.MoveJobs.SingleAsync(candidate => candidate.Id == jobId);
        job.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
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
