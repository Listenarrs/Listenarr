using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class MoveJobProcessorTests
{
    [WindowsFact]
    public async Task ProcessJobAsync_ForeignBasePathAlias_DoesNotInventFinalizedMoveEvidence()
    {
        var source = FileService.GetTempDirectory("move-processor-foreign-base-finalized-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetWindowsRootRelativeTempPath(
            "move-processor-foreign-base-finalized-dst");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Foreign Base Finalized Recovery",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(
            audiobook,
            target,
            source,
            deleteEmptySource: true);
        Directory.CreateDirectory(target);
        File.Copy(sourceFile, Path.Join(target, "book.m4b"));
        Directory.Delete(source, recursive: true);
        var foreignTarget = TempFileService
            .GetWindowsRootRelativeForeignAlias(target);
        audiobook.BasePath = foreignTarget;
        await _audiobookRepository.UpdateAsync(audiobook);
        Assert.True(job.Phase < MoveJobPhase.Published);

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(job, CancellationToken.None);

        var persisted = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.NotEqual(MoveJobStatus.Completed, persisted.Status);
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_MarkerlessVerifiedCopy_Completes()
    {
        var state = await CreateMarkerlessFinalizedCopyStateAsync();
        var processor = _provider.GetRequiredService<IMoveJobProcessor>();

        await processor.ProcessJobAsync(state.Job, CancellationToken.None);

        var persisted = Assert.IsType<MoveJob>(
            await state.Queue.GetJobAsync(state.Job.Id));
        Assert.True(
            persisted.Status == MoveJobStatus.Completed,
            $"Expected Completed but found {persisted.Status}: {persisted.Error}");
        Assert.Null(persisted.Error);
        Assert.Equal(
            MoveJobEntryCleanupState.Retained,
            persisted.SourceDirectoryCleanupState);
        Assert.All(
            persisted.Entries.Where(entry =>
                entry.EntryType == MoveJobEntryType.File
                && !MoveManifestIdentity.IsBoundaryAuthorization(entry)),
            entry => Assert.Equal(
                MoveJobEntryCleanupState.Deleted,
                entry.CleanupState));
        Assert.False(MoveJobPublicProjection.IsSourceRetained(persisted));
        Assert.True(File.Exists(Path.Join(state.Target, "book.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_TransientMarkerlessVerificationFailure_SchedulesRetry()
    {
        var state = await CreateMarkerlessFinalizedCopyStateAsync();
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailFinalizedVerificationOnce());
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingService);

        await processor.ProcessJobAsync(state.Job, CancellationToken.None);

        var retryJob = Assert.IsType<MoveJob>(
            await state.Queue.GetJobAsync(state.Job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retryJob.Status);
        Assert.NotNull(retryJob.NextAttemptAt);
        await MakeRetryDueAsync(state.Job.Id);
        var generation = Assert.IsType<int>(
            await state.Queue.TryClaimJobAsync(state.Job.Id, LeaseOwner));
        retryJob.LeaseOwner = LeaseOwner;
        retryJob.LeaseGeneration = generation;

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(retryJob, CancellationToken.None);

        Assert.Equal(
            MoveJobStatus.Completed,
            (await state.Queue.GetJobAsync(state.Job.Id))?.Status);
    }

    [Fact]
    public async Task ProcessJobAsync_MarkerlessCorruptedCopy_RequiresAttention()
    {
        var state = await CreateMarkerlessFinalizedCopyStateAsync();
        await File.WriteAllTextAsync(Path.Join(state.Target, "book.m4b"), "corrupted audio");
        var processor = _provider.GetRequiredService<IMoveJobProcessor>();

        await processor.ProcessJobAsync(state.Job, CancellationToken.None);

        Assert.Equal(
            MoveJobStatus.NeedsAttention,
            (await state.Queue.GetJobAsync(state.Job.Id))?.Status);
        Assert.Equal(
            "corrupted audio",
            await File.ReadAllTextAsync(Path.Join(state.Target, "book.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_MarkerlessCopyWithUnownedFile_RequiresAttention()
    {
        var state = await CreateMarkerlessFinalizedCopyStateAsync();
        var unownedFile = await FileService.GetFileAsync(
            state.Target,
            "operator-note.txt",
            "preserve me");
        var processor = _provider.GetRequiredService<IMoveJobProcessor>();

        await processor.ProcessJobAsync(state.Job, CancellationToken.None);

        Assert.Equal(
            MoveJobStatus.NeedsAttention,
            (await state.Queue.GetJobAsync(state.Job.Id))?.Status);
        Assert.True(File.Exists(unownedFile));
        Assert.Equal("preserve me", await File.ReadAllTextAsync(unownedFile));
    }

    [DirectoryLinkFact]
    public async Task ProcessJobAsync_MarkerlessCopyWithLinkedTarget_RequiresAttention()
    {
        var state = await CreateMarkerlessFinalizedCopyStateAsync();
        var externalTarget = FileService.GetTempDirectory(
            "move-processor-markerless-linked-external");
        var externalFile = await FileService.GetFileAsync(
            externalTarget,
            "book.m4b",
            "verified audio");
        Directory.Delete(state.Target, recursive: true);
        Assert.True(
            TryCreateProcessorDirectoryLink(state.Target, externalTarget),
            "The required directory link could not be created.");

        try
        {
            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(state.Job, CancellationToken.None);

            Assert.Equal(
                MoveJobStatus.NeedsAttention,
                (await state.Queue.GetJobAsync(state.Job.Id))?.Status);
            Assert.True(File.Exists(externalFile));
            Assert.Equal("verified audio", await File.ReadAllTextAsync(externalFile));
        }
        finally
        {
            TryRemoveProcessorDirectoryLink(state.Target);
        }
    }

    [Fact]
    public async Task ProcessJobAsync_MarkerlessCopyWithPartialArtifact_RequiresAttention()
    {
        var state = await CreateMarkerlessFinalizedCopyStateAsync();
        var partialPath = Path.Join(
            state.Target,
            $"book.m4b.listenarr-{state.Job.Id:N}.partial");
        await File.WriteAllTextAsync(partialPath, "verified audio");
        var processor = _provider.GetRequiredService<IMoveJobProcessor>();

        await processor.ProcessJobAsync(state.Job, CancellationToken.None);

        Assert.Equal(
            MoveJobStatus.NeedsAttention,
            (await state.Queue.GetJobAsync(state.Job.Id))?.Status);
        Assert.True(File.Exists(partialPath));
    }

    [Fact]
    public async Task ProcessJobAsync_MarkerlessMissingTarget_RequiresAttention()
    {
        var state = await CreateMarkerlessFinalizedCopyStateAsync();
        Directory.Delete(state.Target, recursive: true);
        var processor = _provider.GetRequiredService<IMoveJobProcessor>();

        await processor.ProcessJobAsync(state.Job, CancellationToken.None);

        Assert.Equal(
            MoveJobStatus.NeedsAttention,
            (await state.Queue.GetJobAsync(state.Job.Id))?.Status);
        Assert.False(Directory.Exists(state.Target));
    }

    [Fact]
    public async Task ProcessJobAsync_MarkerlessRecreatedEmptyTarget_RequiresAttention()
    {
        var state = await CreateMarkerlessFinalizedCopyStateAsync();
        Directory.Delete(state.Target, recursive: true);
        Directory.CreateDirectory(state.Target);
        var processor = _provider.GetRequiredService<IMoveJobProcessor>();

        await processor.ProcessJobAsync(state.Job, CancellationToken.None);

        Assert.Equal(
            MoveJobStatus.NeedsAttention,
            (await state.Queue.GetJobAsync(state.Job.Id))?.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(state.Target));
    }

    [Fact]
    public async Task ProcessJobAsync_RetryAfterPublishedBeforeSourceCleanup_ResumesFilesystemWorkflow()
    {
        var source = FileService.GetTempDirectory(
            "move-processor-published-before-cleanup-src");
        var sourceFile = await FileService.GetFileAsync(
            source,
            "book.m4b",
            "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-published-before-cleanup-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Published Before Cleanup Recovery",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(
            audiobook,
            target,
            source,
            deleteEmptySource: false);
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new ThrowUnexpectedAfterPublish());
        var faultingProcessor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingService);

        await faultingProcessor.ProcessJobAsync(job, CancellationToken.None);

        var failed = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.Failed, failed.Status);
        Assert.Equal(MoveJobPhase.Published, failed.Phase);
        Assert.True(File.Exists(sourceFile));
        Assert.Equal(
            "verified audio",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));

        var requeuedId = await queue.RequeueMoveAsync(job.Id);
        Assert.Equal(job.Id, requeuedId);
        var requeued = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        await PrepareJobForProcessingAsync(queue, requeued);

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(requeued, CancellationToken.None);

        var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.True(
            completed.Status == MoveJobStatus.Completed,
            completed.Error ?? $"Unexpected recovery status: {completed.Status}");
        Assert.False(File.Exists(sourceFile));
        Assert.True(Directory.Exists(source));
        Assert.Equal(
            "verified audio",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_RetryDuringMarkerlessCopy_ResumesFromDurableJournal()
    {
        var source = FileService.GetTempDirectory(
            "move-processor-partial-copy-src");
        var sourceFile = await FileService.GetFileAsync(
            source,
            "book.m4b",
            "partial copy audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-partial-copy-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Partial Copy Recovery",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(
            audiobook,
            target,
            source,
            deleteEmptySource: false);
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailOnceAtProcessorCopyMutationPoint(
                CopyMutationFaultPoint.AfterMarkerlessFileStateUpdate));
        var faultingProcessor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingService);

        await faultingProcessor.ProcessJobAsync(job, CancellationToken.None);

        var retry = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retry.Status);
        Assert.Equal(MoveJobPhase.Copying, retry.Phase);
        Assert.True(File.Exists(sourceFile));
        await MakeRetryDueAsync(job.Id);
        var generation = Assert.IsType<int>(
            await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        retry.LeaseOwner = LeaseOwner;
        retry.LeaseGeneration = generation;

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(retry, CancellationToken.None);

        var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.True(
            completed.Status == MoveJobStatus.Completed,
            completed.Error ?? $"Unexpected recovery status: {completed.Status}");
        Assert.False(File.Exists(sourceFile));
        Assert.True(Directory.Exists(source));
        Assert.Equal(
            "partial copy audio",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_RetryDuringForcedRetention_ResumesWithoutDeletingSource()
    {
        var source = FileService.GetTempDirectory(
            "move-processor-partial-retention-src");
        var firstSource = await FileService.GetFileAsync(
            source,
            "book-1.m4b",
            "audio one");
        var secondSource = await FileService.GetFileAsync(
            source,
            "book-2.m4b",
            "audio two");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-partial-retention-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Partial Retention Recovery",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(
            audiobook,
            target,
            source,
            deleteEmptySource: true,
            sourceCleanupMode: MoveSourceCleanupMode.RetainSource,
            forceCopyAndRetainSource: true);
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailOnceDuringProcessorRetention());
        var faultingProcessor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingService);

        await faultingProcessor.ProcessJobAsync(job, CancellationToken.None);

        var retry = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retry.Status);
        Assert.Equal(MoveJobPhase.CleaningSource, retry.Phase);
        Assert.True(File.Exists(firstSource));
        Assert.True(File.Exists(secondSource));
        Assert.Contains(
            retry.Entries.Where(entry => entry.EntryType == MoveJobEntryType.File),
            entry => entry.CleanupState == MoveJobEntryCleanupState.Retained);
        Assert.Contains(
            retry.Entries.Where(entry => entry.EntryType == MoveJobEntryType.File),
            entry => entry.CleanupState == MoveJobEntryCleanupState.Pending);
        await MakeRetryDueAsync(job.Id);
        var generation = Assert.IsType<int>(
            await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        retry.LeaseOwner = LeaseOwner;
        retry.LeaseGeneration = generation;

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(retry, CancellationToken.None);

        var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.True(
            completed.Status == MoveJobStatus.Completed,
            completed.Error ?? $"Unexpected recovery status: {completed.Status}");
        Assert.True(MoveJobPublicProjection.IsSourceRetained(completed));
        Assert.Equal("audio one", await File.ReadAllTextAsync(firstSource));
        Assert.Equal("audio two", await File.ReadAllTextAsync(secondSource));
        Assert.Equal(
            "audio one",
            await File.ReadAllTextAsync(Path.Join(target, "book-1.m4b")));
        Assert.Equal(
            "audio two",
            await File.ReadAllTextAsync(Path.Join(target, "book-2.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_ForcedRetentionWithDestructiveRecoveryEvidence_FailsClosed()
    {
        var source = FileService.GetTempDirectory(
            "move-processor-retention-corruption-src");
        var firstSource = await FileService.GetFileAsync(
            source,
            "book-1.m4b",
            "audio one");
        var secondSource = await FileService.GetFileAsync(
            source,
            "book-2.m4b",
            "audio two");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-retention-corruption-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Forced Retention Corruption",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(
            audiobook,
            target,
            source,
            deleteEmptySource: true,
            sourceCleanupMode: MoveSourceCleanupMode.RetainSource,
            forceCopyAndRetainSource: true);
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailOnceDuringProcessorRetention());
        var faultingProcessor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingService);

        await faultingProcessor.ProcessJobAsync(job, CancellationToken.None);

        var retry = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retry.Status);
        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var retained = await db.MoveJobEntries.FirstAsync(entry =>
                entry.MoveJobId == job.Id
                && entry.EntryType == MoveJobEntryType.File
                && entry.CleanupState == MoveJobEntryCleanupState.Retained);
            retained.CleanupState = MoveJobEntryCleanupState.DeleteAuthorized;
            await db.SaveChangesAsync();
        }
        await MakeRetryDueAsync(job.Id);
        retry = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        var generation = Assert.IsType<int>(
            await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        retry.LeaseOwner = LeaseOwner;
        retry.LeaseGeneration = generation;

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(retry, CancellationToken.None);

        var blocked = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.NeedsAttention, blocked.Status);
        Assert.Contains(
            "Forced source retention",
            blocked.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("audio one", await File.ReadAllTextAsync(firstSource));
        Assert.Equal("audio two", await File.ReadAllTextAsync(secondSource));
        Assert.Equal(
            "audio one",
            await File.ReadAllTextAsync(Path.Join(target, "book-1.m4b")));
        Assert.Equal(
            "audio two",
            await File.ReadAllTextAsync(Path.Join(target, "book-2.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_RetryAfterSourceDeleteBeforeStateUpdate_ResumesCleanup()
    {
        var source = FileService.GetTempDirectory(
            "move-processor-source-delete-recovery-src");
        var sourceFile = await FileService.GetFileAsync(
            source,
            "book.m4b",
            "delete recovery audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-source-delete-recovery-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Source Delete Recovery",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(
            audiobook,
            target,
            source,
            deleteEmptySource: false);
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new FailOnceAtProcessorSourceCleanupPoint(
                SourceCleanupFaultPoint.AfterMarkerlessSourceFileDeleteBeforeStateUpdate));
        var faultingProcessor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            faultingService);

        await faultingProcessor.ProcessJobAsync(job, CancellationToken.None);

        var retry = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.RetryScheduled, retry.Status);
        Assert.Equal(MoveJobPhase.CleaningSource, retry.Phase);
        Assert.False(File.Exists(sourceFile));
        Assert.Contains(
            retry.Entries.Where(entry => entry.EntryType == MoveJobEntryType.File),
            entry => entry.CleanupState == MoveJobEntryCleanupState.DeleteAuthorized);
        Assert.Equal(
            "delete recovery audio",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        await MakeRetryDueAsync(job.Id);
        var generation = Assert.IsType<int>(
            await queue.TryClaimJobAsync(job.Id, LeaseOwner));
        retry.LeaseOwner = LeaseOwner;
        retry.LeaseGeneration = generation;

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(retry, CancellationToken.None);

        var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.True(
            completed.Status == MoveJobStatus.Completed,
            completed.Error ?? $"Unexpected recovery status: {completed.Status}");
        Assert.False(File.Exists(sourceFile));
        Assert.True(Directory.Exists(source));
        Assert.Equal(
            "delete recovery audio",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task ProcessJobAsync_MarkerlessPublishedCopy_ResumesFullFinalization()
    {
        var source = FileService.GetTempDirectory("move-processor-markerless-unfinalized-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-markerless-unfinalized-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Markerless Unfinalized Recovery",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(
            audiobook,
            target,
            source,
            deleteEmptySource: false);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var request = CreateMoveRequest(source, target, job, deleteEmptySource: false);
        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        var persistedJob = Assert.IsType<MoveJob>(
            await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobPhase.Finalizing, persistedJob.Phase);
        var processor = _provider.GetRequiredService<IMoveJobProcessor>();

        await processor.ProcessJobAsync(persistedJob, CancellationToken.None);

        Assert.Equal(
            MoveJobStatus.Completed,
            (await queue.GetJobAsync(job.Id))?.Status);
        using var verificationScope = _provider.CreateScope();
        var verificationRepository = verificationScope.ServiceProvider
            .GetRequiredService<IAudiobookRepository>();
        var updated = Assert.IsType<Audiobook>(
            await verificationRepository.GetByIdAsync(audiobook.Id));
        Assert.Equal(
            Path.GetFullPath(target),
            Path.GetFullPath(Assert.IsType<string>(updated.BasePath)));
        Assert.Single(
            await _historyRepository.GetByCorrelationIdAsync($"move:{job.Id:N}"),
            entry => entry.EventType == "Moved");
    }

    [Fact]
    public async Task ProcessJobAsync_MarkerlessAtomicMove_WithPersistedManifest_Completes()
    {
        var source = FileService.GetTempDirectory("move-processor-markerless-atomic-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"move-processor-markerless-atomic-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Markerless Atomic Recovery",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var request = CreateMoveRequest(source, target, job, deleteEmptySource: true);
        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        audiobook.BasePath = target;
        await _audiobookRepository.UpdateAsync(audiobook);
        await service.FinalizeMoveAsync(request, result, CancellationToken.None);
        var processor = _provider.GetRequiredService<IMoveJobProcessor>();

        await processor.ProcessJobAsync(job, CancellationToken.None);

        Assert.Equal(
            MoveJobStatus.Completed,
            (await queue.GetJobAsync(job.Id))?.Status);
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("empty")]
    [InlineData("replaced")]
    public async Task ProcessJobAsync_MarkerlessAtomicTargetChanged_RequiresAttention(
        string mutation)
    {
        var source = FileService.GetTempDirectory($"move-processor-atomic-changed-{mutation}-src");
        await FileService.GetFileAsync(source, "book.m4b", "original audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"move-processor-atomic-changed-{mutation}-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Markerless Atomic Changed",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var request = CreateMoveRequest(source, target, job, deleteEmptySource: true);
        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        audiobook.BasePath = target;
        await _audiobookRepository.UpdateAsync(audiobook);
        await service.FinalizeMoveAsync(request, result, CancellationToken.None);
        Directory.Delete(target, recursive: true);
        if (!string.Equals(mutation, "deleted", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(target);
            if (string.Equals(mutation, "replaced", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(
                    Path.Join(target, "replacement.txt"),
                    "unrelated content");
            }
        }

        await _provider.GetRequiredService<IMoveJobProcessor>()
            .ProcessJobAsync(job, CancellationToken.None);

        Assert.Equal(
            MoveJobStatus.NeedsAttention,
            (await queue.GetJobAsync(job.Id))?.Status);
        Assert.False(File.Exists(Path.Join(target, "book.m4b")));
    }

    private async Task<MarkerlessFinalizedCopyState> CreateMarkerlessFinalizedCopyStateAsync()
    {
        var source = FileService.GetTempDirectory("move-processor-markerless-copy-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-markerless-copy-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Markerless Copy Recovery",
            BasePath = source
        });
        var (queue, job) = await CreateQueuedMoveJobAsync(
            audiobook,
            target,
            source,
            deleteEmptySource: false);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var request = CreateMoveRequest(source, target, job, deleteEmptySource: false);
        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        audiobook.BasePath = target;
        await _audiobookRepository.UpdateAsync(audiobook);
        await service.FinalizeMoveAsync(request, result, CancellationToken.None);
        result.TargetVerificationLease?.Dispose();
        return new MarkerlessFinalizedCopyState(queue, job, source, target);
    }

    private static AudiobookContentMoveRequest CreateMoveRequest(
        string source,
        string target,
        MoveJob job,
        bool deleteEmptySource) =>
        new(
            source,
            target,
            job.Id,
            deleteEmptySource,
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemPathSemantics.CurrentHostDefault,
            new MoveLeaseToken(LeaseOwner, job.LeaseGeneration));

    private static bool TryCreateProcessorDirectoryLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void TryRemoveProcessorDirectoryLink(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to remove processor test directory link '{linkPath}': {exception.Message}");
        }
    }

    private sealed class FailOnceAtProcessorCopyMutationPoint(
        CopyMutationFaultPoint expectedPoint) : IMoveFaultInjector
    {
        private int _failed;

        public void OnCopyMutation(
            Guid jobId,
            CopyMutationFaultPoint faultPoint)
        {
            if (faultPoint == expectedPoint
                && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new IOException(
                    $"Injected processor copy interruption at {faultPoint}.");
            }
        }
    }

    private sealed class FailOnceAtProcessorSourceCleanupPoint(
        SourceCleanupFaultPoint expectedPoint) : IMoveFaultInjector
    {
        private int _failed;

        public void OnSourceCleanupMutation(
            Guid jobId,
            SourceCleanupFaultPoint faultPoint)
        {
            if (faultPoint == expectedPoint
                && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new IOException(
                    $"Injected processor source-cleanup interruption at {faultPoint}.");
            }
        }
    }

    private sealed class FailOnceDuringProcessorRetention : IMoveFaultInjector
    {
        private int _failed;

        public void OnSourceRetentionMutation(
            Guid jobId,
            SourceRetentionFaultPoint faultPoint)
        {
            if (faultPoint == SourceRetentionFaultPoint.AfterEntryStateUpdate
                && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new IOException(
                    "Injected processor retention interruption after durable state update.");
            }
        }
    }

    private sealed class FailFinalizedVerificationOnce : IMoveFaultInjector
    {
        private bool _failed;

        public void OnFinalizedVerification(
            Guid jobId,
            FinalizedVerificationFaultPoint faultPoint)
        {
            if (_failed
                || faultPoint != FinalizedVerificationFaultPoint.BeforeManifestVerification)
            {
                return;
            }

            _failed = true;
            throw new IOException("Simulated transient target verification lock.");
        }
    }

    private sealed record MarkerlessFinalizedCopyState(
        IMoveQueueService Queue,
        MoveJob Job,
        string Source,
        string Target);
}
