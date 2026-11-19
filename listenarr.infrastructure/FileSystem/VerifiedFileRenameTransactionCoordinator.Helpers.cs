using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class VerifiedFileRenameTransactionCoordinator
{
    internal enum RootContractValidation
    {
        Valid,
        Unavailable,
        Mismatch
    }

    private async Task PersistNewJournalAsync(
        VerifiedFileRenameJournal journal,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        if (await db.VerifiedFileRenameJournals
            .AsNoTracking()
            .AnyAsync(
                candidate => candidate.OperationId == journal.OperationId,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The verified organize operation ID is already in use.");
        }

        db.VerifiedFileRenameJournals.Add(journal);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal async Task<VerifiedFileRenameJournal?> GetJournalAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        return await db.VerifiedFileRenameJournals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                journal => journal.OperationId == operationId,
                cancellationToken);
    }

    internal async Task AdvanceAsync(
        Guid operationId,
        VerifiedFileRenameState state,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var journal = await db.VerifiedFileRenameJournals
            .SingleAsync(
                candidate => candidate.OperationId == operationId,
                cancellationToken);
        if (!CanAdvance(journal.State, state))
        {
            throw new InvalidOperationException(
                $"Verified organize journal {operationId} cannot advance from {journal.State} to {state}.");
        }

        journal.State = state;
        journal.Error = error;
        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal async Task MarkNeedsAttentionAsync(
        Guid operationId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var journal = await db.VerifiedFileRenameJournals
            .SingleOrDefaultAsync(
                candidate => candidate.OperationId == operationId,
                cancellationToken);
        if (journal == null || IsTerminal(journal.State))
        {
            return;
        }

        journal.State = VerifiedFileRenameState.NeedsAttention;
        journal.Error = error;
        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal async Task MarkSourceRetainedAsync(
        Guid operationId,
        string reason,
        CancellationToken cancellationToken)
    {
        await AdvanceAsync(
            operationId,
            VerifiedFileRenameState.CompletedSourceRetained,
            reason,
            cancellationToken);
    }

    internal static bool IsTerminal(VerifiedFileRenameState state) =>
        state is VerifiedFileRenameState.Completed
            or VerifiedFileRenameState.CompletedSourceRetained
            or VerifiedFileRenameState.RolledBack
            or VerifiedFileRenameState.NeedsAttention;

    private static bool CanAdvance(
        VerifiedFileRenameState current,
        VerifiedFileRenameState next)
    {
        if (current == next)
        {
            return true;
        }
        if (next == VerifiedFileRenameState.NeedsAttention)
        {
            return !IsTerminal(current);
        }

        return current switch
        {
            VerifiedFileRenameState.Planned => next is
                VerifiedFileRenameState.TargetVerified or
                VerifiedFileRenameState.RolledBack,
            VerifiedFileRenameState.TargetVerified => next is
                VerifiedFileRenameState.OwnerMetadataReconciled or
                VerifiedFileRenameState.RolledBack,
            VerifiedFileRenameState.OwnerMetadataReconciled => next is
                VerifiedFileRenameState.SourceQuarantined or
                VerifiedFileRenameState.CompletedSourceRetained,
            VerifiedFileRenameState.SourceQuarantined => next is
                VerifiedFileRenameState.SourceDeleted or
                VerifiedFileRenameState.CompletedSourceRetained,
            VerifiedFileRenameState.SourceDeleted => next is
                VerifiedFileRenameState.Completed or
                VerifiedFileRenameState.CompletedSourceRetained,
            _ => false
        };
    }

    private static async Task CopyAndVerifyAsync(
        PinnedDirectoryCreation.PinnedFileEntry source,
        PinnedDirectoryCreation.PinnedFileEntry target,
        FilePublicationSourceProof sourceProof,
        CancellationToken cancellationToken)
    {
        await using var input = source.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        await using var output = target.OpenWriteStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        if (!await target.MatchesAsync(
                sourceProof.Length,
                sourceProof.Sha256,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The verified organize staging file failed SHA-256 verification.");
        }
    }

    private async Task TryRollbackPreparedTargetAsync(
        Guid operationId,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? destinationParent,
        PinnedDirectoryCreation.PinnedFileEntry? targetEntry,
        CancellationToken cancellationToken,
        bool stagingNeedsAttention = false)
    {
        try
        {
            if (targetEntry != null && destinationParent != null)
            {
                var visibility = targetEntry.ProbeVisiblePathMatch();
                if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
                {
                    throw new IOException(
                        "The verified organize staging/target is temporarily unavailable during rollback.");
                }
                if (visibility == RegistrationPublicationMatchOutcome.Match)
                {
                    targetEntry.Delete(immediateWindows: true);
                    destinationParent.FlushDirectoryEntry();
                }
            }

            if (stagingNeedsAttention)
            {
                await MarkNeedsAttentionAsync(operationId,
                    "Verified organize fallback staging could not be removed safely; surviving files were preserved for repair.",
                    cancellationToken);
            }
            else
            {
                await AdvanceAsync(
                    operationId,
                    VerifiedFileRenameState.RolledBack,
                    error: null,
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException
                or StackOverflowException))
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "Verified organize preparation rollback requires attention: "
                    + exception.Message,
                CancellationToken.None);
        }
    }

    internal async Task<RootContractValidation> ValidateRootContractsAsync(
        VerifiedFileRenameJournal journal,
        CancellationToken cancellationToken)
    {
        var roots = await rootFolderRepository.GetAllAsync();
        var sourceRoot = journal.SourceRootFolderId > 0
            ? roots.SingleOrDefault(root => root.Id == journal.SourceRootFolderId)
            : null;
        var destinationRoot = journal.DestinationRootFolderId > 0
            ? roots.SingleOrDefault(
                root => root.Id == journal.DestinationRootFolderId)
            : null;

        if ((journal.SourceRootFolderId > 0
                && (sourceRoot == null
                    || sourceRoot.StorageContractRevision
                        != journal.SourceStorageContractRevision))
            || (journal.DestinationRootFolderId > 0
                && (destinationRoot == null
                    || destinationRoot.StorageContractRevision
                        != journal.DestinationStorageContractRevision)))
        {
            return RootContractValidation.Mismatch;
        }

        try
        {
            if (sourceRoot != null)
            {
                var sourceHealth = await storageHealthResolver.ResolveAsync(
                    sourceRoot,
                    cancellationToken);
                if (!sourceHealth.CanRetireVerifiedSource)
                {
                    return sourceHealth.State is RootFolderStorageState.Missing
                            or RootFolderStorageState.Changed
                        ? RootContractValidation.Mismatch
                        : RootContractValidation.Unavailable;
                }
            }

            if (destinationRoot != null)
            {
                var destinationHealth = await storageHealthResolver.ResolveAsync(
                    destinationRoot,
                    cancellationToken);
                if (!destinationHealth.CanPublishAdditively)
                {
                    return destinationHealth.State is RootFolderStorageState.Missing
                            or RootFolderStorageState.Changed
                        ? RootContractValidation.Mismatch
                        : RootContractValidation.Unavailable;
                }
            }

            return RootContractValidation.Valid;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException)
        {
            return RootContractValidation.Unavailable;
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor
        OpenOrCreateVerifiedDestinationParent(RootFolder? destinationRoot, string destinationParentPath) =>
        PinnedDirectoryCreation.OpenPinnedConfiguredHierarchy(
            destinationRoot, destinationParentPath, createMissing: true);
    internal static IReadOnlyList<string> ResolveDestinationHierarchySegments(
        string rootPath,
        string destinationParentPath,
        FileSystemPathSemantics semantics)
    {
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                rootPath,
                destinationParentPath,
                semantics,
                out var relative))
        {
            throw new InvalidOperationException(
                "The verified organize destination parent could not be resolved relative to its configured root semantics.");
        }
        if (string.IsNullOrEmpty(relative))
        {
            return [];
        }

        var separators = semantics.Syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        var segments = relative.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                "The verified organize destination hierarchy contains a traversal segment.");
        }

        return segments;
    }

    private static RootFolder? FindContainingRoot(
        string path,
        IReadOnlyCollection<RootFolder> roots)
    {
        var fullPath = Path.GetFullPath(path);
        RootFolder? best = null;
        var bestLength = -1;
        var unavailableLength = -1;
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(fullPath, out var syntax))
            throw new InvalidOperationException("The organize path syntax cannot be authorized.");
        foreach (var root in roots)
        {
            var persisted = RootFolderPathSemantics.ResolvePersisted(root);
            if (!persisted.HasValue
                || persisted.Value.DetectAmbiguousCaseMatches
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var rootPath,
                    out _)
                || string.IsNullOrWhiteSpace(rootPath))
            {
                if (FileSystemPathIdentity.StoredBoundaryMayContainPath(
                        root.Path, fullPath, syntax, root.CaseSensitivityMode)
                    || (!string.IsNullOrWhiteSpace(root.Path.Trim())
                        && FileSystemPathIdentity.StoredBoundaryMayContainPath(
                            root.Path.Trim(), fullPath, syntax, root.CaseSensitivityMode)))
                    unavailableLength = Math.Max(unavailableLength, root.Path.Length);
                continue;
            }
            if (!FileSystemPathIdentity.IsSameOrInside(fullPath, rootPath, persisted.Value.Semantics)) continue;

            if (rootPath.Length > bestLength)
            {
                best = root;
                bestLength = rootPath.Length;
            }
        }

        if (unavailableLength >= bestLength && unavailableLength >= 0)
            throw new InvalidOperationException("The organize path overlaps an unresolved configured boundary.");
        return best;
    }
}
