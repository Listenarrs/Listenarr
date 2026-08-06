using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Theory]
    [InlineData((int)OwnershipMarkerWriteFaultPoint.BeforeTemporaryFileCreation)]
    [InlineData((int)OwnershipMarkerWriteFaultPoint.DuringJsonWrite)]
    [InlineData((int)OwnershipMarkerWriteFaultPoint.DuringFlush)]
    [InlineData((int)OwnershipMarkerWriteFaultPoint.AfterTemporaryFileWritten)]
    [InlineData((int)OwnershipMarkerWriteFaultPoint.BeforePublication)]
    public async Task MoveContentsAsync_TempOwnershipPublicationFailure_RetriesCleanly(
        int faultPointValue)
    {
        var faultPoint = (OwnershipMarkerWriteFaultPoint)faultPointValue;
        var source = FileService.GetTempDirectory("content-move-temp-marker-fault-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-temp-marker-fault-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new SingleOwnershipPublicationFaultInjector(
                OwnershipMarkerKind.TemporaryDirectory,
                faultPoint));

        var publicationException = await Assert.ThrowsAnyAsync<IOException>(() =>
            faultingService.MoveContentsAsync(request, CancellationToken.None));
        Assert.IsNotType<MoveNeedsAttentionException>(publicationException);

        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(target));
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_InterruptedTempOwnershipPublication_RetriesWithoutManualCleanup()
    {
        var source = FileService.GetTempDirectory("content-move-temp-marker-recovery-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-temp-marker-recovery-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var faultingService = CreateOwnershipFaultingService(
            OwnershipMarkerKind.TemporaryDirectory);

        var publicationException = await Assert.ThrowsAnyAsync<IOException>(() =>
            faultingService.MoveContentsAsync(request, CancellationToken.None));
        Assert.IsNotType<MoveNeedsAttentionException>(publicationException);

        Assert.True(Directory.Exists(source));
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.False(Directory.Exists(source));
        Assert.True(result.SourceCleanupCompleted);
    }

    [WindowsFact]
    public async Task MoveContentsAsync_RecoveredOwnershipWrite_DoesNotMutateAttributesBeforePinnedPublication()
    {
        var source = FileService.GetTempDirectory(
            "content-move-recovered-marker-attributes-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-recovered-marker-attributes-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var faultingService = CreateOwnershipFaultingService(
            OwnershipMarkerKind.TemporaryDirectory);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            faultingService.MoveContentsAsync(request, CancellationToken.None));
        var tempDirectory = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var writePath = Assert.Single(Directory.EnumerateFiles(
            tempDirectory,
            ".listenarr-temp-owner.json.writing-*"));
        File.SetAttributes(writePath, FileAttributes.Normal);
        var recoveryService = CreateMoveService(
            new SingleOwnershipPublicationFaultInjector(
                OwnershipMarkerKind.TemporaryDirectory,
                OwnershipMarkerWriteFaultPoint.BeforeRecoveredPublication));

        await Assert.ThrowsAsync<IOException>(() =>
            recoveryService.MoveContentsAsync(request, CancellationToken.None));

        Assert.False(
            File.GetAttributes(writePath).HasFlag(FileAttributes.Hidden));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
        Assert.False(File.Exists(Path.Join(tempDirectory, ".listenarr-temp-owner.json")));
    }

    [Fact]
    public async Task MoveContentsAsync_ReplacedRecoveredOwnershipWrite_IsPreserved()
    {
        var source = FileService.GetTempDirectory("content-move-recovered-marker-replacement-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-recovered-marker-replacement-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var faultingService = CreateOwnershipFaultingService(
            OwnershipMarkerKind.TemporaryDirectory);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            faultingService.MoveContentsAsync(request, CancellationToken.None));
        var tempDirectory = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var writePath = Assert.Single(Directory.EnumerateFiles(
            tempDirectory,
            ".listenarr-temp-owner.json.writing-*"));
        var originalGeneration = writePath + ".original";
        var replacement = await File.ReadAllTextAsync(writePath);
        var recoveryService = CreateMoveService(
            new ReplaceRecoveredOwnershipWrite(
                writePath,
                originalGeneration,
                replacement));

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            recoveryService.MoveContentsAsync(request, CancellationToken.None));

        Assert.Equal(replacement, await File.ReadAllTextAsync(writePath));
        Assert.Equal(replacement, await File.ReadAllTextAsync(originalGeneration));
        Assert.False(File.Exists(Path.Join(tempDirectory, ".listenarr-temp-owner.json")));
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }

    [Fact]
    public async Task ResumeSourceCleanup_InterruptedQuarantineOwnershipPublication_RecoversOrphanWriteMarker()
    {
        var source = FileService.GetTempDirectory("content-move-quarantine-marker-recovery-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-quarantine-marker-recovery-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var faultingService = CreateOwnershipFaultingService(
            OwnershipMarkerKind.QuarantineDirectory);

        var publicationException = await Assert.ThrowsAnyAsync<IOException>(() =>
            faultingService.MoveContentsAsync(request, CancellationToken.None));
        Assert.IsNotType<MoveNeedsAttentionException>(publicationException);

        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{request.JobId:N}");
        Assert.True(Directory.Exists(quarantineRoot));
        Assert.Single(Directory.EnumerateFiles(
            quarantineRoot,
            ".listenarr-quarantine-owner.json.writing-*"));

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var recovered = await service.GetRecoverableMoveAsync(
            request,
            CancellationToken.None);
        Assert.NotNull(recovered);

        var completed = await service.ResumeSourceCleanupAsync(
            request,
            recovered!,
            CancellationToken.None);

        Assert.True(completed.SourceCleanupCompleted);
        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(quarantineRoot));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Theory]
    [InlineData((int)OwnershipCleanupFaultPoint.BeforeCleanupDirectoryMove)]
    [InlineData((int)OwnershipCleanupFaultPoint.BeforeOwnershipMarkerDelete)]
    [InlineData((int)OwnershipCleanupFaultPoint.BeforeDirectoryDelete)]
    [InlineData((int)OwnershipCleanupFaultPoint.BeforeTombstoneDelete)]
    public async Task ResumeSourceCleanup_InterruptedQuarantineCleanup_UsesCleanupTombstone(
        int faultPointValue)
    {
        var faultPoint = (OwnershipCleanupFaultPoint)faultPointValue;
        var source = FileService.GetTempDirectory("content-move-quarantine-tombstone-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-quarantine-tombstone-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new OwnershipCleanupFaultInjector(faultPoint));

        await Assert.ThrowsAsync<IOException>(() =>
            faultingService.MoveContentsAsync(request, CancellationToken.None));

        var sourceParent = Path.GetDirectoryName(source)!;
        var quarantineRoot = Path.Join(
            sourceParent,
            $".listenarr-quarantine-{request.JobId:N}");
        var cleanupDirectory = Path.Join(
            sourceParent,
            $".listenarr-quarantine-directory-{request.JobId:N}.cleanup-dir");
        var tombstonePath = Path.Join(
            sourceParent,
            $".listenarr-quarantine-directory-{request.JobId:N}.cleanup.json");
        Assert.Equal(
            faultPoint == OwnershipCleanupFaultPoint.BeforeCleanupDirectoryMove,
            Directory.Exists(quarantineRoot));
        Assert.Equal(
            faultPoint is OwnershipCleanupFaultPoint.BeforeOwnershipMarkerDelete
                or OwnershipCleanupFaultPoint.BeforeDirectoryDelete,
            Directory.Exists(cleanupDirectory));
        Assert.True(File.Exists(tombstonePath));
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var recovered = await service.GetRecoverableMoveAsync(
            request,
            CancellationToken.None);
        Assert.NotNull(recovered);

        var completed = await service.ResumeSourceCleanupAsync(
            request,
            recovered!,
            CancellationToken.None);

        Assert.True(completed.SourceCleanupCompleted);
        Assert.False(Directory.Exists(quarantineRoot));
        Assert.False(Directory.Exists(cleanupDirectory));
        Assert.False(File.Exists(tombstonePath));
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Theory]
    [InlineData((int)OwnershipCleanupFaultPoint.BeforeDirectoryDelete)]
    [InlineData((int)OwnershipCleanupFaultPoint.BeforeTombstoneDelete)]
    public async Task MoveContentsAsync_OriginalOwnedPathRecreatedDuringCleanup_PreservesEvidence(
        int faultPointValue)
    {
        var faultPoint = (OwnershipCleanupFaultPoint)faultPointValue;
        var source = FileService.GetTempDirectory("content-move-cleanup-recreated-path-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-cleanup-recreated-path-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var sourceParent = Path.GetDirectoryName(source)!;
        var quarantineRoot = Path.Join(
            sourceParent,
            $".listenarr-quarantine-{request.JobId:N}");
        var cleanupDirectory = Path.Join(
            sourceParent,
            $".listenarr-quarantine-directory-{request.JobId:N}.cleanup-dir");
        var tombstonePath = Path.Join(
            sourceParent,
            $".listenarr-quarantine-directory-{request.JobId:N}.cleanup.json");
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new RecreateOwnedPathDuringCleanup(quarantineRoot, faultPoint));

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("recreated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(quarantineRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(quarantineRoot));
        Assert.Equal(
            faultPoint == OwnershipCleanupFaultPoint.BeforeDirectoryDelete,
            Directory.Exists(cleanupDirectory));
        Assert.True(File.Exists(tombstonePath));
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_TombstonedDirectoryRecreatedWithContent_IsPreserved()
    {
        var source = FileService.GetTempDirectory("content-move-tombstone-recreated-src");
        await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-tombstone-recreated-dst-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var faultingService = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new OwnershipCleanupFaultInjector(
                OwnershipCleanupFaultPoint.BeforeDirectoryDelete));

        await Assert.ThrowsAsync<IOException>(() =>
            faultingService.MoveContentsAsync(request, CancellationToken.None));

        var sourceParent = Path.GetDirectoryName(source)!;
        var quarantineRoot = Path.Join(
            sourceParent,
            $".listenarr-quarantine-{request.JobId:N}");
        var cleanupDirectory = Path.Join(
            sourceParent,
            $".listenarr-quarantine-directory-{request.JobId:N}.cleanup-dir");
        var tombstonePath = Path.Join(
            sourceParent,
            $".listenarr-quarantine-directory-{request.JobId:N}.cleanup.json");
        Directory.CreateDirectory(quarantineRoot);
        var unexpectedFile = await FileService.GetFileAsync(
            quarantineRoot,
            "operator-note.txt",
            "preserve me");
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request, CancellationToken.None));

        Assert.Contains("recreated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve me", await File.ReadAllTextAsync(unexpectedFile));
        Assert.True(File.Exists(tombstonePath));
        Assert.True(Directory.Exists(quarantineRoot));
        Assert.True(Directory.Exists(cleanupDirectory));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    private AudiobookContentMoveService CreateOwnershipFaultingService(
        OwnershipMarkerKind markerKind) =>
        new(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new OwnershipPublicationFaultInjector(markerKind));

    private sealed class RecreateOwnedPathDuringCleanup(
        string originalDirectory,
        OwnershipCleanupFaultPoint expectedFaultPoint) : IMoveFaultInjector
    {
        private bool _recreated;

        public void OnOwnershipCleanup(
            Guid jobId,
            OwnershipMarkerKind markerKind,
            OwnershipCleanupFaultPoint faultPoint)
        {
            if (_recreated
                || markerKind != OwnershipMarkerKind.QuarantineDirectory
                || faultPoint != expectedFaultPoint)
            {
                return;
            }

            Directory.CreateDirectory(originalDirectory);
            _recreated = true;
        }
    }

    private sealed class OwnershipCleanupFaultInjector(
        OwnershipCleanupFaultPoint expectedFaultPoint) : IMoveFaultInjector
    {
        private bool _failed;

        public void OnOwnershipCleanup(
            Guid jobId,
            OwnershipMarkerKind markerKind,
            OwnershipCleanupFaultPoint faultPoint)
        {
            if (_failed
                || markerKind != OwnershipMarkerKind.QuarantineDirectory
                || faultPoint != expectedFaultPoint)
            {
                return;
            }

            _failed = true;
            throw new IOException("Simulated process stop before owned directory deletion.");
        }
    }

    private sealed class SingleOwnershipPublicationFaultInjector(
        OwnershipMarkerKind expectedMarkerKind,
        OwnershipMarkerWriteFaultPoint expectedFaultPoint) : IMoveFaultInjector
    {
        private bool _failed;

        public void OnOwnershipMarkerWrite(
            Guid jobId,
            OwnershipMarkerKind markerKind,
            OwnershipMarkerWriteFaultPoint faultPoint)
        {
            if (_failed
                || markerKind != expectedMarkerKind
                || faultPoint != expectedFaultPoint)
            {
                return;
            }

            _failed = true;
            throw new IOException($"Simulated ownership publication failure at {faultPoint}.");
        }
    }

    private sealed class OwnershipPublicationFaultInjector(
        OwnershipMarkerKind markerKind) : IMoveFaultInjector
    {
        public void OnOwnershipMarkerWrite(
            Guid jobId,
            OwnershipMarkerKind currentMarkerKind,
            OwnershipMarkerWriteFaultPoint faultPoint)
        {
            if (currentMarkerKind == markerKind
                && faultPoint is OwnershipMarkerWriteFaultPoint.BeforePublication
                    or OwnershipMarkerWriteFaultPoint.BeforeTemporaryFileDeletion)
            {
                throw new IOException($"Simulated ownership publication failure at {faultPoint}.");
            }
        }
    }

    private sealed class ReplaceRecoveredOwnershipWrite(
        string writePath,
        string originalGeneration,
        string replacement) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnOwnershipMarkerWrite(
            Guid jobId,
            OwnershipMarkerKind markerKind,
            OwnershipMarkerWriteFaultPoint faultPoint)
        {
            if (_replaced
                || markerKind != OwnershipMarkerKind.TemporaryDirectory
                || faultPoint != OwnershipMarkerWriteFaultPoint.BeforeRecoveredPublication)
            {
                return;
            }

            File.Move(writePath, originalGeneration);
            File.WriteAllText(writePath, replacement);
            _replaced = true;
        }
    }
}
