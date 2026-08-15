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

        Assert.Equal(
            MoveJobStatus.Completed,
            (await state.Queue.GetJobAsync(state.Job.Id))?.Status);
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
