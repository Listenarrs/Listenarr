using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task DeleteOriginalSourceAsync(
        string source,
        string target,
        bool targetInsideSource,
        bool deleteEmptySource,
        Guid jobId,
        MoveLeaseToken leaseToken,
        IReadOnlyList<MoveJobEntry> manifest,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        LibraryDirectoryOwnership? targetDirectoryOwnership,
        string? sourceCleanupBoundary,
        IReadOnlyDictionary<string, string>? sourcePhysicalObjectIdentities,
        CancellationToken cancellationToken)
    {
        var sourceExists = Directory.Exists(source);
        if (sourceExists && IsFilesystemRoot(source, sourceSemantics))
        {
            throw new MoveNeedsAttentionException(
                "Source path became invalid before cleanup.");
        }

        var sourceParent = Path.GetDirectoryName(source)
            ?? throw new MoveNeedsAttentionException(
                "Source parent path is unavailable.");
        var quarantineRoot = Path.Join(sourceParent, $".listenarr-quarantine-{jobId:N}");
        if (!FileSystemSafety.TryValidateMutationTarget(
            quarantineRoot,
            [sourceParent],
            out quarantineRoot,
            out var quarantineReason))
        {
            throw new MoveNeedsAttentionException(quarantineReason);
        }

        var cleanupRequest = new AudiobookContentMoveRequest(
            source,
            target,
            jobId,
            deleteEmptySource,
            sourceSemantics,
            targetSemantics,
            leaseToken,
            sourceCleanupBoundary,
            targetDirectoryOwnership,
            sourcePhysicalObjectIdentities);
        var ownedSourceDirectories = await LoadOwnedSourceDirectoriesForCleanupAsync(
            source,
            sourceSemantics,
            cancellationToken);
        ValidatedQuarantineOwnership? quarantineOwnership = null;
        if (Directory.Exists(quarantineRoot))
        {
            quarantineOwnership = await CreateOrValidateOwnedQuarantineDirectoryAsync(
                quarantineRoot,
                sourceParent,
                jobId,
                source,
                target,
                sourceSemantics,
                targetSemantics,
                leaseToken,
                cancellationToken);
        }
        else if (File.Exists(quarantineRoot))
        {
            throw new MoveNeedsAttentionException(
                "The move quarantine path is occupied by a file and cannot be used safely.");
        }

        if (quarantineOwnership != null)
        {
            sourceExists = await RecoverEmptySourceDirectoryQuarantineAsync(
                cleanupRequest,
                quarantineOwnership,
                sourceParent,
                cancellationToken);
        }

        var expectedAtSource = new List<MoveJobEntry>();
        foreach (var directoryEntry in manifest
            .Where(entry => entry.EntryType == MoveJobEntryType.Directory)
            .Where(entry => !IsRootManifestEntry(entry))
            .Where(entry => FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                source,
                entry.RelativePath,
                sourceSemantics,
                out var sourceDirectory)
                && Directory.Exists(sourceDirectory)))
        {
            expectedAtSource.Add(directoryEntry);
        }
        foreach (var entry in manifest.Where(entry =>
            entry.EntryType == MoveJobEntryType.File
            && entry.CleanupState != MoveJobEntryCleanupState.Deleted))
        {
            ResolveCleanupPaths(
                source,
                quarantineRoot,
                entry.RelativePath,
                sourceSemantics,
                out var sourceFile,
                out var quarantineFile);
            if (quarantineOwnership != null)
            {
                ValidateQuarantineMutationPath(quarantineOwnership, quarantineFile);
            }

            if (File.Exists(sourceFile))
            {
                if (File.Exists(quarantineFile))
                {
                    throw new MoveNeedsAttentionException(
                        $"Both source and quarantine copies exist; cleanup is ambiguous: {entry.RelativePath}");
                }

                expectedAtSource.Add(entry);
                continue;
            }

            if (entry.CleanupState == MoveJobEntryCleanupState.Quarantined
                && !File.Exists(quarantineFile))
            {
                if (entry.CleanupProtectionVersion
                        >= DestinationRetentionCleanupProtectionVersion)
                {
                    if (!await TryCompleteMissingQuarantineRetentionAsync(
                            cleanupRequest,
                            source,
                            target,
                            entry,
                            targetSemantics,
                            cancellationToken))
                    {
                        throw new MoveNeedsAttentionException(
                            $"Source and quarantine files are absent without the persisted destination retention guard: {entry.RelativePath}");
                    }
                }
                else
                {
                    // Compatibility for active jobs persisted before destination
                    // retention was introduced. The source is already absent, so
                    // the only safe convergence available is to reverify the exact
                    // destination bytes before accepting the legacy transition.
                    await VerifyPublishedManifestAsync(
                        target,
                        [entry],
                        targetSemantics,
                        cancellationToken);
                }

                await UpdateCleanupStateAsync(
                    jobId,
                    leaseToken,
                    entry.RelativePath,
                    MoveJobEntryCleanupState.Deleted,
                    cancellationToken);
                entry.CleanupState = MoveJobEntryCleanupState.Deleted;
                continue;
            }

            if (!File.Exists(quarantineFile)
                || !string.Equals(
                    await ComputeSha256Async(quarantineFile, cancellationToken),
                    entry.Sha256,
                    StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    $"Source file disappeared without a verified quarantine copy: {entry.RelativePath}");
            }
        }

        if (sourceExists && expectedAtSource.Count > 0)
        {
            await ValidatePersistedSourceManifestAsync(
                source,
                target,
                targetInsideSource,
                expectedAtSource,
                sourceSemantics,
                cancellationToken,
                requireTrackedFile: false);
        }

        var publishedTempOwnership = await TryValidatePublishedTempOwnershipAsync(
            target,
            cleanupRequest,
            source,
            target,
            cancellationToken);
        ValidateExistingDestinationContents(
            source,
            target,
            manifest,
            jobId,
            targetSemantics,
            publishedTempOwnership,
            quarantineOwnership,
            allowPartialFiles: false,
            targetDirectoryOwnership: targetDirectoryOwnership);
        await VerifyPublishedManifestAsync(
            target,
            manifest,
            targetSemantics,
            cancellationToken);
        quarantineOwnership ??= await CreateOrValidateOwnedQuarantineDirectoryAsync(
            quarantineRoot,
            sourceParent,
            jobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            leaseToken,
            cancellationToken);
        foreach (var entry in manifest.Where(entry =>
            entry.EntryType == MoveJobEntryType.File
            && entry.CleanupState != MoveJobEntryCleanupState.Deleted))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCleanupPaths(
                source,
                quarantineRoot,
                entry.RelativePath,
                sourceSemantics,
                out var sourceFile,
                out var quarantineFile);
            ValidateQuarantineMutationPath(quarantineOwnership, quarantineFile);

            ValidateQuarantineMutationPath(quarantineOwnership, quarantineFile);
            if (!File.Exists(quarantineFile))
            {
                if (!File.Exists(sourceFile))
                {
                    if (entry.CleanupState == MoveJobEntryCleanupState.Quarantined
                        && await TryCompleteMissingQuarantineRetentionAsync(
                            cleanupRequest,
                            source,
                            target,
                            entry,
                            targetSemantics,
                            cancellationToken))
                    {
                        await UpdateCleanupStateAsync(
                            jobId,
                            leaseToken,
                            entry.RelativePath,
                            MoveJobEntryCleanupState.Deleted,
                            cancellationToken);
                        continue;
                    }

                    throw new MoveNeedsAttentionException(
                        $"Source file disappeared before cleanup without durable target retention: {entry.RelativePath}");
                }

                await RevalidateSourceToQuarantineMoveAsync(
                    source,
                    target,
                    sourceFile,
                    quarantineFile,
                    quarantineRoot,
                    sourceParent,
                    jobId,
                    leaseToken,
                    entry,
                    manifest,
                    publishedTempOwnership,
                    targetDirectoryOwnership,
                    sourceSemantics,
                    targetSemantics,
                    cancellationToken);
                faultInjector?.OnSourceCleanupMutation(
                    jobId,
                    SourceCleanupFaultPoint.BeforeSourceFileMove);
                quarantineOwnership = await RevalidateSourceToQuarantineMoveAsync(
                    source,
                    target,
                    sourceFile,
                    quarantineFile,
                    quarantineRoot,
                    sourceParent,
                    jobId,
                    leaseToken,
                    entry,
                    manifest,
                    publishedTempOwnership,
                    targetDirectoryOwnership,
                    sourceSemantics,
                    targetSemantics,
                    cancellationToken);
                await MoveSourceFileToPinnedQuarantineAsync(
                    cleanupRequest,
                    source,
                    target,
                    sourceFile,
                    quarantineFile,
                    quarantineRoot,
                    entry,
                    sourceSemantics,
                    cancellationToken);
            }

            ValidateQuarantineMutationPath(quarantineOwnership, quarantineFile);
            var quarantinedHash = await ComputeSha256Async(
                quarantineFile,
                cancellationToken);
            if (!string.Equals(quarantinedHash, entry.Sha256, StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    $"Quarantined source bytes changed before cleanup and were preserved: {entry.RelativePath}");
            }

            await VerifyPublishedManifestAsync(
                target,
                [entry],
                targetSemantics,
                cancellationToken);
            await UpdateCleanupStateAsync(
                jobId,
                leaseToken,
                entry.RelativePath,
                MoveJobEntryCleanupState.Quarantined,
                cancellationToken);
            await RevalidateQuarantineDeleteAsync(
                source,
                target,
                quarantineFile,
                quarantineRoot,
                sourceParent,
                jobId,
                leaseToken,
                entry,
                manifest,
                publishedTempOwnership,
                targetDirectoryOwnership,
                sourceSemantics,
                targetSemantics,
                cancellationToken);
            faultInjector?.OnSourceCleanupMutation(
                jobId,
                SourceCleanupFaultPoint.BeforeQuarantineFileDelete);
            quarantineOwnership = await RevalidateQuarantineDeleteAsync(
                source,
                target,
                quarantineFile,
                quarantineRoot,
                sourceParent,
                jobId,
                leaseToken,
                entry,
                manifest,
                publishedTempOwnership,
                targetDirectoryOwnership,
                sourceSemantics,
                targetSemantics,
                cancellationToken);
            await DeletePinnedQuarantineFileAsync(
                cleanupRequest,
                source,
                target,
                quarantineFile,
                quarantineRoot,
                entry,
                sourceSemantics,
                targetSemantics,
                cancellationToken);
            await UpdateCleanupStateAsync(
                jobId,
                leaseToken,
                entry.RelativePath,
                MoveJobEntryCleanupState.Deleted,
                cancellationToken);
        }

        foreach (var directoryEntry in manifest
            .Where(entry => entry.EntryType == MoveJobEntryType.Directory)
            .Where(entry => !IsRootManifestEntry(entry))
            .OrderByDescending(entry => entry.RelativePath.Length)
            .Select(entry => new
            {
                Directory = FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    source,
                    entry.RelativePath,
                    sourceSemantics,
                    out var directory)
                    ? directory
                    : null
            })
            .Where(entry => entry.Directory != null
                && Directory.Exists(entry.Directory)
                && !ownedSourceDirectories.Any(ownership =>
                    FileSystemPathIdentity.AreEquivalent(
                        ownership.CanonicalPath,
                        entry.Directory,
                        sourceSemantics))
                && !Directory.EnumerateFileSystemEntries(entry.Directory).Any()))
        {
            await EnsureMutationAuthorizedAsync(
                cleanupRequest,
                source,
                target,
                cancellationToken);
            DeleteValidatedEmptySourceDirectory(
                source,
                directoryEntry.Directory!,
                sourceSemantics);
        }

        await CleanupOwnedSourceDirectoriesAsync(
            cleanupRequest,
            source,
            target,
            ownedSourceDirectories,
            sourceSemantics,
            cancellationToken);

        if (deleteEmptySource
            && sourceExists
            && Directory.Exists(source)
            && !ownedSourceDirectories.Any(ownership =>
                FileSystemPathIdentity.AreEquivalent(
                    ownership.CanonicalPath,
                    source,
                    sourceSemantics))
            && !IsSourceCleanupBoundary(source, sourceCleanupBoundary, sourceSemantics)
            && !Directory.EnumerateFileSystemEntries(source).Any())
        {
            await QuarantineAndDeleteEmptySourceDirectoryAsync(
                cleanupRequest,
                quarantineOwnership,
                sourceParent,
                cancellationToken);
        }

        foreach (var directoryEntry in manifest
            .Where(entry => entry.EntryType == MoveJobEntryType.Directory)
            .Where(entry => !IsRootManifestEntry(entry))
            .OrderByDescending(entry => entry.RelativePath.Length))
        {
            if (FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    quarantineRoot,
                    directoryEntry.RelativePath,
                    sourceSemantics,
                    out var quarantineDirectory)
                && Directory.Exists(quarantineDirectory)
                && !Directory.EnumerateFileSystemEntries(quarantineDirectory).Any())
            {
                await EnsureMutationAuthorizedAsync(
                    cleanupRequest,
                    source,
                    target,
                    cancellationToken);
                DeleteValidatedEmptyQuarantineDirectory(
                    quarantineOwnership,
                    quarantineDirectory);
            }
        }

        await EnsureMutationAuthorizedAsync(
            cleanupRequest,
            source,
            target,
            cancellationToken);
        await DeleteEmptyOwnedQuarantineDirectoryAsync(
            quarantineOwnership,
            jobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            leaseToken,
            cancellationToken);
    }
}
