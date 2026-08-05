using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Theory]
    [InlineData(nameof(RecoveryMarkerWriteFaultPoint.BeforeTemporaryFileCreation))]
    [InlineData(nameof(RecoveryMarkerWriteFaultPoint.DuringJsonWrite))]
    [InlineData(nameof(RecoveryMarkerWriteFaultPoint.DuringFlush))]
    [InlineData(nameof(RecoveryMarkerWriteFaultPoint.AfterTemporaryFileWritten))]
    [InlineData(nameof(RecoveryMarkerWriteFaultPoint.BeforePublication))]
    public async Task MoveContentsAsync_RecoveryMarkerPublicationFailure_PreservesPreviousMarker(
        string faultPointName)
    {
        var faultPoint = Enum.Parse<RecoveryMarkerWriteFaultPoint>(faultPointName);
        var source = FileService.GetTempDirectory($"content-move-marker-{faultPoint}-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory($"content-move-marker-{faultPoint}-dst");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        await WriteRecoveryMarkerAsync(target, jobId, source, target, "copy-started");
        var markerPath = Path.Join(target, $".listenarr-move-{jobId:N}.pending");
        var previousMarker = await File.ReadAllTextAsync(markerPath);
        var injector = new RecoveryMarkerFaultInjector(faultPoint);
        var service = CreateMoveService(injector);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Equal(previousMarker, await File.ReadAllTextAsync(markerPath));
        Assert.Contains("copy-started", await File.ReadAllTextAsync(markerPath));
        Assert.Empty(Directory.EnumerateFiles(target, $".listenarr-move-{jobId:N}.pending.writing-*"));
        Assert.True(File.Exists(sourceFile));

        var retryService = _provider.GetRequiredService<AudiobookContentMoveService>();
        await retryService.MoveContentsAsync(request, CancellationToken.None);
        Assert.False(Directory.Exists(source));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_RecoveryMarkerTempCleanupFailure_LeavesNonAuthoritativeOrphan()
    {
        var source = FileService.GetTempDirectory("content-move-marker-cleanup-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-marker-cleanup-dst");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        await WriteRecoveryMarkerAsync(target, jobId, source, target, "copy-started");
        var markerPath = Path.Join(target, $".listenarr-move-{jobId:N}.pending");
        var previousMarker = await File.ReadAllTextAsync(markerPath);
        var injector = new RecoveryMarkerFaultInjector(
            RecoveryMarkerWriteFaultPoint.BeforePublication,
            RecoveryMarkerWriteFaultPoint.BeforeTemporaryFileDeletion);
        var service = CreateMoveService(injector);

        var exception = await Assert.ThrowsAnyAsync<IOException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.IsNotType<MoveNeedsAttentionException>(exception);
        Assert.Contains("could not be restored cleanly", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previousMarker, await File.ReadAllTextAsync(markerPath));
        var orphan = Assert.Single(
            Directory.EnumerateFiles(target, $".listenarr-move-{jobId:N}.pending.writing-*"));
        Assert.NotEmpty(await File.ReadAllTextAsync(orphan));

        var recoveryService = _provider.GetRequiredService<AudiobookContentMoveService>();
        var recovery = await recoveryService.GetRecoverableMoveAsync(request);
        Assert.NotNull(recovery);
        Assert.False(recovery!.SourceCleanupCompleted);
        Assert.True(File.Exists(sourceFile));
        Assert.False(File.Exists(orphan));

        var completed = await recoveryService.ResumeSourceCleanupAsync(
            request,
            recovery,
            CancellationToken.None);
        Assert.True(completed.SourceCleanupCompleted);
        Assert.False(Directory.Exists(source));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_RecoveryMarkerReplacedBeforePinnedRead_IsPreservedAndRequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-marker-read-swap-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-marker-read-swap-dst");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        await WriteRecoveryMarkerAsync(target, jobId, source, target, "copy-started");
        var markerPath = Path.Join(target, $".listenarr-move-{jobId:N}.pending");
        var replacement = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1,
            JobId = Guid.NewGuid(),
            Source = Path.GetFullPath(source),
            Target = Path.GetFullPath(target),
            Stage = "copy-started"
        });
        var replaced = false;
        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(path =>
        {
            if (replaced
                || !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(target),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            replaced = true;
            File.Delete(markerPath);
            File.WriteAllText(markerPath, replacement);
        });
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.True(replaced);
        Assert.Contains("different job", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(replacement, await File.ReadAllTextAsync(markerPath));
        Assert.True(File.Exists(sourceFile));
        Assert.False(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task MoveContentsAsync_RecoveryMarkerReplacedBeforeStageUpdate_IsPreservedAndRequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-marker-swap-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-marker-swap-dst");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        await WriteRecoveryMarkerAsync(target, jobId, source, target, "copy-started");
        var markerPath = Path.Join(target, $".listenarr-move-{jobId:N}.pending");
        var replacement = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1,
            JobId = Guid.NewGuid(),
            Source = Path.GetFullPath(source),
            Target = Path.GetFullPath(target),
            Stage = "copy-started"
        });
        var service = CreateMoveService(
            new ReplaceRecoveryMarkerBeforePublication(markerPath, replacement));

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("different job", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(replacement, await File.ReadAllTextAsync(markerPath));
        Assert.True(File.Exists(sourceFile));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        Assert.Empty(Directory.EnumerateFiles(
            target,
            $".listenarr-move-{jobId:N}.pending.writing-*"));
    }

    [Fact]
    public async Task MoveContentsAsync_RecoveryMarkerAdvancedBeforeStageUpdate_IsPreserved()
    {
        var source = FileService.GetTempDirectory("content-move-marker-advanced-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-marker-advanced-dst");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        await WriteRecoveryMarkerAsync(target, jobId, source, target, "copy-started");
        var markerPath = Path.Join(target, $".listenarr-move-{jobId:N}.pending");
        var replacement = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1,
            JobId = jobId,
            Source = Path.GetFullPath(source),
            Target = Path.GetFullPath(target),
            Stage = "source-cleanup-complete"
        });
        var service = CreateMoveService(
            new ReplaceRecoveryMarkerBeforePublication(markerPath, replacement));

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("later or incompatible stage", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(replacement, await File.ReadAllTextAsync(markerPath));
        Assert.True(File.Exists(sourceFile));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [WindowsFact]
    public async Task MoveContentsAsync_ReplacesExistingHiddenRecoveryMarkerAtomicallyOnWindows()
    {

        var source = FileService.GetTempDirectory("content-move-hidden-marker-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-hidden-marker-dst");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        await WriteRecoveryMarkerAsync(target, jobId, source, target, "copy-started");
        var markerPath = Path.Join(target, $".listenarr-move-{jobId:N}.pending");
        File.SetAttributes(markerPath, File.GetAttributes(markerPath) | FileAttributes.Hidden);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.False(Directory.Exists(source));
        Assert.Contains("source-cleanup-complete", await File.ReadAllTextAsync(markerPath));
        Assert.True((File.GetAttributes(markerPath) & FileAttributes.Hidden) != 0);
    }

    private AudiobookContentMoveService CreateMoveService(IMoveFaultInjector faultInjector)
    {
        return new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            faultInjector);
    }

    private sealed class ReplaceRecoveryMarkerBeforePublication(
        string markerPath,
        string replacement) : IMoveFaultInjector
    {
        private bool _replaced;

        public void OnRecoveryMarkerWrite(
            Guid jobId,
            RecoveryMarkerWriteFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != RecoveryMarkerWriteFaultPoint.BeforePublication)
            {
                return;
            }

            File.WriteAllText(markerPath, replacement);
            _replaced = true;
        }
    }

    private sealed class RecoveryMarkerFaultInjector(
        params RecoveryMarkerWriteFaultPoint[] faultPoints) : IMoveFaultInjector
    {
        private readonly HashSet<RecoveryMarkerWriteFaultPoint> _faultPoints = [.. faultPoints];

        public void OnRecoveryMarkerWrite(
            Guid jobId,
            RecoveryMarkerWriteFaultPoint faultPoint)
        {
            if (_faultPoints.Contains(faultPoint))
            {
                throw new IOException($"Injected recovery marker failure at {faultPoint}.");
            }
        }
    }
}
