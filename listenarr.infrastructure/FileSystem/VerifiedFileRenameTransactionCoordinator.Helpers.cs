using System.ComponentModel;
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
        CancellationToken cancellationToken)
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

            await AdvanceAsync(
                operationId,
                VerifiedFileRenameState.RolledBack,
                error: null,
                cancellationToken);
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
        var sourceRoot = roots.SingleOrDefault(
            root => root.Id == journal.SourceRootFolderId);
        var destinationRoot = roots.SingleOrDefault(
            root => root.Id == journal.DestinationRootFolderId);
        if (sourceRoot == null || destinationRoot == null
            || sourceRoot.StorageContractRevision
                != journal.SourceStorageContractRevision
            || destinationRoot.StorageContractRevision
                != journal.DestinationStorageContractRevision)
        {
            return RootContractValidation.Mismatch;
        }

        try
        {
            var sourceHealth = await storageHealthResolver.ResolveAsync(
                sourceRoot,
                cancellationToken);
            var destinationHealth = await storageHealthResolver.ResolveAsync(
                destinationRoot,
                cancellationToken);
            if (!sourceHealth.CanRetireVerifiedSource
                || !destinationHealth.CanPublishAdditively)
            {
                return sourceHealth.State is RootFolderStorageState.Missing
                        or RootFolderStorageState.Changed
                    || destinationHealth.State is RootFolderStorageState.Missing
                        or RootFolderStorageState.Changed
                    ? RootContractValidation.Mismatch
                    : RootContractValidation.Unavailable;
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
        OpenOrCreateVerifiedDestinationParent(
            RootFolder destinationRoot,
            string destinationParentPath)
    {
        var persisted = RootFolderPathSemantics.ResolvePersisted(destinationRoot)
            ?? throw new InvalidOperationException(
                "The verified organize destination root has no persisted path semantics.");
        if (persisted.DetectAmbiguousCaseMatches
            || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                destinationRoot.Path,
                out var rootPath,
                out _)
            || !FileSystemPathIdentity.IsSameOrInside(
                destinationParentPath,
                rootPath,
                persisted.Semantics))
        {
            throw new InvalidOperationException(
                "The verified organize destination parent is outside its configured root.");
        }

        var current = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(rootPath);
        try
        {
            var segments = ResolveDestinationHierarchySegments(
                rootPath,
                destinationParentPath,
                persisted.Semantics);
            if (segments.Count == 0)
            {
                return current;
            }

            foreach (var segment in segments)
            {
                PinnedDirectoryCreation.PinnedDirectoryAnchor next;
                try
                {
                    next = current.OpenExistingChild(segment);
                }
                catch (Win32Exception exception) when (
                    exception.NativeErrorCode is 2 or 3)
                {
                    using var creation = current.TryCreateChild(segment);
                    next = creation.Created
                        ? creation.OpenCreatedDirectoryAnchor()
                        : current.OpenExistingChild(segment);
                }

                if (!next.VisiblePathMatches())
                {
                    next.Dispose();
                    throw new InvalidOperationException(
                        "The verified organize destination hierarchy changed during additive creation.");
                }

                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

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
        foreach (var root in roots)
        {
            var persisted = RootFolderPathSemantics.ResolvePersisted(root);
            if (!persisted.HasValue
                || persisted.Value.DetectAmbiguousCaseMatches
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var rootPath,
                    out _)
                || string.IsNullOrWhiteSpace(rootPath)
                || !FileSystemPathIdentity.IsSameOrInside(
                    fullPath,
                    rootPath,
                    persisted.Value.Semantics))
            {
                continue;
            }

            if (rootPath.Length > bestLength)
            {
                best = root;
                bestLength = rootPath.Length;
            }
        }

        return best;
    }
}
