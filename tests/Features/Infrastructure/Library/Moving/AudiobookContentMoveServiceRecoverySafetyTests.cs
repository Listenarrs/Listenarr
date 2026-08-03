using System.Text.Json;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task GetRecoverableMoveAsync_AtomicMarkerWithSourceAndTarget_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-both-src");
        await FileService.GetFileAsync(source, "book.m4b", "source audio");
        var target = FileService.GetTempDirectory("content-move-atomic-both-dst");
        await FileService.GetFileAsync(target, "book.m4b", "target audio");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await WriteRecoveryMarkerAsync(target, jobId, source, target, "atomic-rename-complete");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request));

        Assert.Contains("Both source and target exist", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("source audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        Assert.Equal("target audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_AtomicMarkerBeforeRename_DoesNotRecover()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-before-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"content-move-atomic-before-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await WriteRecoveryMarkerAsync(source, jobId, source, target, "atomic-rename-complete");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.GetRecoverableMoveAsync(request);

        Assert.Null(result);
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task MoveContentsAsync_AuthoritativeAtomicMarkerBeforeRename_ResumesSafely()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-before-resume-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            Path.GetDirectoryName(source)!,
            $"content-move-atomic-before-resume-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await WriteRecoveryMarkerAsync(
            source,
            jobId,
            source,
            target,
            "atomic-rename-complete");

        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            new DisableAtomicRename());
        var result = await service.MoveContentsAsync(request, CancellationToken.None);

        Assert.True(result.SourceCleanupCompleted);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.True(File.Exists(result.RecoveryMarkerPath));
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.NotEmpty(await db.MoveJobEntries
            .Where(entry => entry.MoveJobId == jobId)
            .ToListAsync());
    }

    [Fact]
    public async Task MoveContentsAsync_SourceAtomicMarkerWithExistingTarget_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-conflict-src");
        await FileService.GetFileAsync(source, "book.m4b", "source audio");
        var target = FileService.GetTempDirectory("content-move-atomic-conflict-dst");
        await FileService.GetFileAsync(target, "operator-note.txt", "preserve me");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await WriteRecoveryMarkerAsync(
            source,
            jobId,
            source,
            target,
            "atomic-rename-complete");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.Contains("conflicts with existing target", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
        Assert.Equal("preserve me", await File.ReadAllTextAsync(Path.Join(target, "operator-note.txt")));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_MissingSourceAndTarget_DoesNotRecover()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-neither-src");
        var target = Path.Join(FileService.GetTempPath(), $"content-move-atomic-neither-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        Directory.Delete(source, recursive: true);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var result = await service.GetRecoverableMoveAsync(request);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_LegacyAtomicMarker_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-legacy-atomic-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"content-move-legacy-atomic-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await File.WriteAllTextAsync(
            Path.Join(source, $".listenarr-move-{jobId:N}.pending"),
            "atomic-rename-complete");
        Directory.Move(source, target);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request));

        Assert.Contains("obsolete pre-release", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_AtomicMarkerWithWrongIdentity_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-wrong-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"content-move-atomic-wrong-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await WriteRecoveryMarkerAsync(
            source,
            Guid.NewGuid(),
            source,
            target,
            "atomic-rename-complete",
            markerFileJobId: jobId);
        Directory.Move(source, target);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request));
    }

    [LinuxFact]
    public async Task GetRecoverableMoveAsync_AmbiguousPersistedMarkerPath_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-ambiguous-recovery-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-ambiguous-recovery-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        var ambiguousSource = "/" + Path.GetFullPath(source);
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousSource,
            out _));
        await File.WriteAllTextAsync(
            Path.Join(source, $".listenarr-move-{jobId:N}.pending"),
            JsonSerializer.Serialize(new
            {
                Version = 1,
                JobId = jobId,
                Source = ambiguousSource,
                Target = Path.GetFullPath(target),
                Stage = "atomic-rename-complete"
            }));
        Directory.Move(source, target);

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            _provider.GetRequiredService<AudiobookContentMoveService>()
                .GetRecoverableMoveAsync(request));

        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        Assert.True(File.Exists(Path.Join(
            target,
            $".listenarr-move-{jobId:N}.pending")));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_AtomicMarkerWithPersistedManifest_Recovers()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-manifest-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"content-move-atomic-manifest-dst-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        await WriteRecoveryMarkerAsync(source, jobId, source, target, "atomic-rename-complete");
        Directory.Move(source, target);

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var recovered = await service.GetRecoverableMoveAsync(request);

        Assert.NotNull(recovered);
        Assert.True(recovered.SourceCleanupCompleted);
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_UnreadableMarker_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-unreadable-marker-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-unreadable-marker-dst");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await File.WriteAllTextAsync(
            Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
            "{ truncated");

        var service = _provider.GetRequiredService<AudiobookContentMoveService>();
        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(request));

        Assert.Contains("corrupt or truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Join(source, "book.m4b")));
    }

    [FileLinkFact]
    public async Task GetRecoverableMoveAsync_LinkedRecoveryMarker_PreservesExternalMarker()
    {
        var source = FileService.GetTempDirectory("content-move-linked-marker-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = FileService.GetTempDirectory("content-move-linked-marker-dst");
        var external = FileService.GetTempDirectory("content-move-linked-marker-external");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        var externalMarker = Path.Join(external, "marker.json");
        await File.WriteAllTextAsync(
            externalMarker,
            JsonSerializer.Serialize(new
            {
                Version = 1,
                JobId = jobId,
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                Stage = "copy-started"
            }));
        var markerPath = Path.Join(target, $".listenarr-move-{jobId:N}.pending");
        try
        {
            File.CreateSymbolicLink(markerPath, externalMarker);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException(
                $"This native filesystem regression requires symbolic-link support: {exception.Message}");
        }

        try
        {
            var original = await File.ReadAllTextAsync(externalMarker);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.GetRecoverableMoveAsync(request));

            Assert.Contains("symbolic link or reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(original, await File.ReadAllTextAsync(externalMarker));
            Assert.True(File.Exists(markerPath));
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
        }
        finally
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
    }

    [DirectoryLinkFact]
    public async Task GetRecoverableMoveAsync_AtomicMarkerWithLinkedTarget_RequiresAttention()
    {
        var source = FileService.GetTempDirectory("content-move-atomic-linked-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var targetParent = FileService.GetTempDirectory("content-move-atomic-linked-parent");
        var target = Path.Join(targetParent, "linked-target");
        var externalTarget = FileService.GetTempDirectory("content-move-atomic-linked-external");
        var externalFile = await FileService.GetFileAsync(externalTarget, "book.m4b", "external audio");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
        await WriteRecoveryMarkerAsync(externalTarget, jobId, source, target, "atomic-rename-complete");
        Directory.Delete(source, recursive: true);
        Assert.True(
            TryCreateDirectoryLink(target, externalTarget),
            "The required directory link could not be created.");

        try
        {
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.GetRecoverableMoveAsync(request));

            Assert.Equal("external audio", await File.ReadAllTextAsync(externalFile));
        }
        finally
        {
            TryRemoveDirectoryLink(target);
        }
    }

    private static Task WriteRecoveryMarkerAsync(
        string directory,
        Guid markerJobId,
        string source,
        string target,
        string stage,
        Guid? markerFileJobId = null)
    {
        var fileJobId = markerFileJobId ?? markerJobId;
        return File.WriteAllTextAsync(
            Path.Join(directory, $".listenarr-move-{fileJobId:N}.pending"),
            JsonSerializer.Serialize(new
            {
                Version = 1,
                JobId = markerJobId,
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                Stage = stage
            }));
    }
}
