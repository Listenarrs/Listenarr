using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Common;

internal static class MoveJobTestFactory
{
    public static async Task<MoveJob> SeedUnresolvedExecutionAsync(
        IServiceProvider services,
        int audiobookId,
        string sourcePath,
        string targetPath,
        MoveJobStatus status = MoveJobStatus.Failed,
        MoveJobPhase phase = MoveJobPhase.Published,
        MoveFailureKind failureKind = MoveFailureKind.Unknown)
    {
        var job = new MoveJob
        {
            Id = Guid.NewGuid(),
            AudiobookId = audiobookId,
            SourcePath = Path.GetFullPath(sourcePath),
            RequestedPath = Path.GetFullPath(targetPath),
            Status = status,
            Phase = phase,
            FailureKind = failureKind,
            Error = "Injected unresolved filesystem execution for regression coverage.",
            EnqueuedAt = DateTime.UtcNow,
            Entries =
            [
                new MoveJobEntry
                {
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = 1,
                    LastWriteTimeUtc = DateTime.UnixEpoch,
                    Sha256 = new string('A', 64),
                    CopyState = MoveJobEntryCopyState.Verified,
                    CleanupState = MoveJobEntryCleanupState.Deleted
                }
            ]
        };
        var factory = services.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.MoveJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    public static async Task<MoveEnqueueCommand> CreateCommandAsync(
        IServiceProvider services,
        int audiobookId,
        string sourcePath,
        string targetPath,
        bool deleteEmptySource = true,
        string? sourceCleanupBoundary = null)
    {
        var resolver = services.GetRequiredService<IFileSystemSemanticsResolver>();
        var sourceResolution = await resolver.ResolveAsync(sourcePath);
        var targetResolution = await resolver.ResolveAsync(targetPath);
        if (sourceResolution.State != PathIdentityState.Valid
            || targetResolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                sourceResolution.Reason
                    ?? targetResolution.Reason
                    ?? "Move test filesystem identity is unavailable.");
        }

        var sourceIdentity = PathIdentitySnapshot.FromResolution(
            sourceResolution.Semantics,
            FileSystemCaseSensitivityMode.Auto,
            sourceResolution.BoundaryPath,
            sourcePath);
        var sourceAuthorizationBoundary = !string.IsNullOrWhiteSpace(sourceCleanupBoundary)
            ? Path.GetFullPath(sourceCleanupBoundary)
            : deleteEmptySource
                ? Path.GetDirectoryName(Path.GetFullPath(sourcePath))
                    ?? sourceResolution.BoundaryPath
                : sourceIdentity.BoundaryPath;
        if (!FileSystemPathIdentity.IsSameOrInside(
                sourcePath,
                sourceAuthorizationBoundary,
                sourceResolution.Semantics))
        {
            throw new InvalidOperationException(
                "Move test source escaped its cleanup/authorization boundary.");
        }
        var targetBoundary = FindTargetBoundary(
            sourcePath,
            targetPath,
            sourceResolution.Semantics);
        var targetIdentity = PathIdentitySnapshot.FromResolution(
            targetResolution.Semantics,
            FileSystemCaseSensitivityMode.Auto,
            targetBoundary,
            targetPath);
        var directoryIdentityResolver =
            services.GetRequiredService<IDirectoryObjectIdentityResolver>();
        var sourceDirectoryIdentity = await directoryIdentityResolver.ResolveAsync(
            sourceAuthorizationBoundary);
        if (!sourceDirectoryIdentity.IsAvailable)
        {
            throw new InvalidOperationException(
                sourceDirectoryIdentity.UnavailableReason
                    ?? "Move test source boundary identity is unavailable.");
        }
        var targetDirectoryIdentity = await directoryIdentityResolver.ResolveAsync(
            targetBoundary);
        if (!targetDirectoryIdentity.IsAvailable)
        {
            throw new InvalidOperationException(
                targetDirectoryIdentity.UnavailableReason
                    ?? "Move test target boundary identity is unavailable.");
        }
        var manifest = await BuildManifestAsync(sourcePath);
        await EnsureTrackedRowsAsync(
            services,
            audiobookId,
            sourcePath,
            sourceIdentity,
            manifest);
        return new MoveEnqueueCommand(
            audiobookId,
            sourcePath,
            sourceIdentity,
            manifest,
            targetPath,
            targetIdentity,
            sourceDirectoryIdentity.Version!.Value,
            sourceDirectoryIdentity.Value!,
            targetDirectoryIdentity.Version!.Value,
            targetDirectoryIdentity.Value!,
            deleteEmptySource,
            deleteEmptySource
                ? sourceAuthorizationBoundary
                : sourceCleanupBoundary);
    }

    private static string FindTargetBoundary(
        string sourcePath,
        string targetPath,
        FileSystemPathSemantics sourceSemantics)
    {
        var source = Path.GetFullPath(sourcePath);
        var current = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current)
                && !FileSystemPathIdentity.IsSameOrInside(
                    current,
                    source,
                    sourceSemantics))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }

        throw new InvalidOperationException(
            "Move test target has no enclosing authorization boundary outside the source tree.");
    }

    private static async Task EnsureTrackedRowsAsync(
        IServiceProvider services,
        int audiobookId,
        string sourcePath,
        PathIdentitySnapshot sourceIdentity,
        IReadOnlyCollection<MoveSourceManifestEntry> manifest)
    {
        var repository = services.GetRequiredService<IAudiobookFileRepository>();
        var existing = await repository.GetByAudiobookIdAsync(audiobookId);
        foreach (var entry in manifest.Where(candidate =>
            candidate.EntryType == MoveJobEntryType.File))
        {
            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    sourcePath,
                    entry.RelativePath,
                    sourceIdentity.Semantics,
                    out var fullPath))
            {
                throw new InvalidOperationException(
                    $"Test move manifest escaped its source root: {entry.RelativePath}");
            }

            var identity = AudiobookFilePathIdentity.CreateValid(
                fullPath,
                sourceIdentity.Semantics,
                sourceIdentity.RequestedMode,
                sourceIdentity.BoundaryPath);
            var tracked = existing.FirstOrDefault(file =>
                !string.IsNullOrWhiteSpace(file.Path)
                && FileSystemPathIdentity.AreEquivalent(
                    file.Path,
                    fullPath,
                    sourceIdentity.Semantics));
            if (tracked != null)
            {
                tracked.ApplyPathIdentity(fullPath, identity);
                ApplyPhysicalObjectIdentity(tracked, fullPath);
                await repository.UpdateAsync(tracked);
                continue;
            }

            tracked = AudiobookFile.CreateUnresolved(fullPath);
            tracked.AudiobookId = audiobookId;
            tracked.ApplyPathIdentity(fullPath, identity);
            ApplyPhysicalObjectIdentity(tracked, fullPath);
            var claim = await repository.ClaimAsync(tracked);
            if (claim.Outcome != AudiobookFileClaimOutcome.Created
                || claim.File == null)
            {
                throw new InvalidOperationException(
                    claim.Reason
                        ?? $"Unable to claim test move file: {fullPath}");
            }

            existing.Add(claim.File);
        }
    }

    private static void ApplyPhysicalObjectIdentity(
        AudiobookFile file,
        string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            Path.GetDirectoryName(fullPath)!,
            createMissing: false);
        using var entry = parent.OpenExistingFileForStableRead(
            Path.GetFileName(fullPath));
        file.ApplyPhysicalObjectIdentity(
            entry.GetObjectIdentity(),
            DateTime.UtcNow);
    }

    private static async Task<IReadOnlyList<MoveSourceManifestEntry>> BuildManifestAsync(
        string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
        {
            return SyntheticManifest();
        }

        var entries = new List<MoveSourceManifestEntry>();
        var pending = new Stack<string>();
        pending.Push(sourcePath);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourcePath, path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    entries.Add(new MoveSourceManifestEntry(
                        relativePath,
                        MoveJobEntryType.Directory,
                        0,
                        Directory.GetLastWriteTimeUtc(path),
                        null));
                    pending.Push(path);
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(path);
                entries.Add(new MoveSourceManifestEntry(
                    relativePath,
                    MoveJobEntryType.File,
                    bytes.LongLength,
                    File.GetLastWriteTimeUtc(path),
                    Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(bytes))));
            }
        }

        return entries.Any(entry => entry.EntryType == MoveJobEntryType.File)
            ? entries
            : SyntheticManifest();
    }

    private static IReadOnlyList<MoveSourceManifestEntry> SyntheticManifest() =>
    [
        new MoveSourceManifestEntry(
            "book.m4b",
            MoveJobEntryType.File,
            1,
            DateTime.UnixEpoch,
            new string('A', 64))
    ];
}
