using System.Data.Common;
using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore
{
    private async Task EnsureLiveFilesystemSemanticsAsync(
        string boundaryPath,
        FileSystemCaseSensitivityMode requestedMode,
        FileSystemPathSemantics expectedSemantics,
        string description,
        CancellationToken cancellationToken)
    {
        var resolution = await _semanticsResolver.ResolveAsync(
            boundaryPath,
            requestedMode,
            cancellationToken);
        if (resolution.State != PathIdentityState.Valid
            || resolution.Semantics.Syntax != expectedSemantics.Syntax
            || resolution.Semantics.CaseSensitivity != expectedSemantics.CaseSensitivity)
        {
            throw new MoveNeedsAttentionException(
                $"The move {description} filesystem semantics changed after the move was authorized.");
        }
    }

    private static void EnsureEquivalentIdentity(
        string persisted,
        string current,
        FileSystemPathSemantics semantics,
        string mismatchMessage,
        string invalidMessage)
    {
        try
        {
            if (!FileSystemPathIdentity.AreEquivalent(persisted, current, semantics))
            {
                throw new MoveNeedsAttentionException(mismatchMessage);
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(invalidMessage);
        }
    }

    private static async Task EnsureRelocationTargetGenerationAuthorizedAsync(
        ListenArrDbContext db,
        Guid relocationId,
        string target,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var relocation = await db.RootFolderRelocations
            .AsNoTracking()
            .Where(candidate => candidate.Id == relocationId)
            .Select(candidate => new
            {
                candidate.ActiveRootFolderId,
                candidate.TargetPath,
                candidate.TargetIdentityEnrollmentState,
                candidate.TargetDirectoryObjectIdentityVersion,
                candidate.TargetDirectoryObjectIdentity,
                candidate.TargetDirectoryObjectIdentityUnavailableReason
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new MoveNeedsAttentionException(
                "The relocation owning this move no longer exists.");
        if (!relocation.ActiveRootFolderId.HasValue
            || relocation.TargetIdentityEnrollmentState
                != TargetIdentityEnrollmentState.Authorized)
        {
            throw new MoveNeedsAttentionException(
                "The relocation target no longer has active physical-directory authorization.");
        }

        string targetRoot;
        try
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    relocation.TargetPath,
                    out targetRoot,
                    out var pathReason)
                || !FileSystemPathIdentity.IsSameOrInside(
                    target,
                    targetRoot,
                    targetSemantics))
            {
                throw new MoveNeedsAttentionException(
                    pathReason
                        ?? "The move target escaped its authorized relocation target root.");
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                $"The relocation target identity is invalid: {exception.Message}");
        }

        try
        {
            using var root = PinnedDirectoryCreation.OpenPinnedBoundary(targetRoot);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(
                    relocation.TargetDirectoryObjectIdentityUnavailableReason)
                || !ManagedDirectoryIdentity.MatchesNativeIdentity(
                    relocation.TargetDirectoryObjectIdentityVersion,
                    relocation.TargetDirectoryObjectIdentity,
                    root.GetDirectoryObjectIdentity())
                || !root.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The relocation target no longer identifies its authorized physical generation.");
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            throw new MoveNeedsAttentionException(
                $"The relocation target physical generation is no longer authorized: {exception.Message}");
        }
    }

    private static async Task EnsureTargetBoundaryGenerationAuthorizedAsync(
        ListenArrDbContext db,
        Guid jobId,
        string targetBoundary,
        CancellationToken cancellationToken)
    {
        var authorizationEntries = await db.MoveJobEntries
            .AsNoTracking()
            .Where(entry => entry.MoveJobId == jobId
                && entry.EntryType == MoveJobEntryType.Directory
                && entry.RelativePath == string.Empty
                && entry.Length > 0
                && entry.Sha256 != null)
            .OrderBy(entry => entry.Id)
            .Select(entry => new
            {
                entry.Length,
                entry.Sha256
            })
            .Take(2)
            .ToListAsync(cancellationToken);
        if (authorizationEntries.Count != 1
            || authorizationEntries[0].Length > int.MaxValue
            || authorizationEntries[0].Sha256 is not { Length: 64 } expectedDigest
            || !expectedDigest.All(Uri.IsHexDigit))
        {
            throw new MoveNeedsAttentionException(
                "The move job lacks one authoritative target-boundary physical-generation proof.");
        }

        try
        {
            using var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(
                targetBoundary);
            cancellationToken.ThrowIfCancellationRequested();
            var nativeIdentity = boundary.GetDirectoryObjectIdentity();
            var currentVersion = (int)authorizationEntries[0].Length;
            var currentValue = ManagedDirectoryIdentity.CreateMarkerless(nativeIdentity);
            var currentDigest = MoveManifestIdentity.ComputeTargetBoundaryAuthorizationDigest(
                currentVersion,
                currentValue);
            if (!string.Equals(
                    currentDigest,
                    expectedDigest,
                    StringComparison.OrdinalIgnoreCase))
            {
                currentDigest = await TryResolveConfiguredRootBoundaryDigestAsync(
                        db,
                        targetBoundary,
                        nativeIdentity,
                        currentVersion,
                        cancellationToken)
                    ?? currentDigest;
            }
            if (!string.Equals(
                    currentDigest,
                    expectedDigest,
                    StringComparison.OrdinalIgnoreCase)
                || !boundary.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The move target boundary no longer identifies its authorized physical generation.");
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            throw new MoveNeedsAttentionException(
                $"The move target boundary physical generation is unavailable: {exception.Message}");
        }
    }

    private static async Task<string?> TryResolveConfiguredRootBoundaryDigestAsync(
        ListenArrDbContext db,
        string targetBoundary,
        string nativeIdentity,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var roots = await db.RootFolders
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        foreach (var root in roots)
        {
            var persisted = RootFolderPathSemantics.ResolvePersisted(root);
            if (persisted == null
                || root.DirectoryObjectIdentityVersion != expectedVersion
                || string.IsNullOrWhiteSpace(root.DirectoryObjectIdentity)
                || !ManagedDirectoryIdentity.MatchesNativeIdentity(
                    root.DirectoryObjectIdentityVersion,
                    root.DirectoryObjectIdentity,
                    nativeIdentity))
            {
                continue;
            }

            try
            {
                if (!FileSystemPathIdentity.AreEquivalent(
                        root.Path,
                        targetBoundary,
                        persisted.Value.Semantics))
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            return MoveManifestIdentity.ComputeTargetBoundaryAuthorizationDigest(
                expectedVersion,
                root.DirectoryObjectIdentity);
        }

        return null;
    }

    private static MoveCreatedDirectoryState AdvanceCreatedDirectoryState(
        MoveCreatedDirectoryState current,
        MoveCreatedDirectoryState requested)
    {
        if (current == requested)
        {
            return current;
        }

        if (current == MoveCreatedDirectoryState.Planned
            && requested is MoveCreatedDirectoryState.Created
                or MoveCreatedDirectoryState.Retained
                or MoveCreatedDirectoryState.Removed)
        {
            return requested;
        }

        if (current == MoveCreatedDirectoryState.Created
            && requested is MoveCreatedDirectoryState.Retained
                or MoveCreatedDirectoryState.Removed)
        {
            return requested;
        }

        throw new MoveNeedsAttentionException(
            $"The persisted move-created directory state cannot transition from {current} to {requested}.");
    }

    private static MoveJobEntryCleanupState AdvanceCleanupState(
        MoveJobEntryCleanupState current,
        MoveJobEntryCleanupState requested)
    {
        if (current == requested)
        {
            return current;
        }

        if (current == MoveJobEntryCleanupState.Pending
            && requested is MoveJobEntryCleanupState.DeleteAuthorized
                or MoveJobEntryCleanupState.Retained)
        {
            return requested;
        }

        if (current == MoveJobEntryCleanupState.DeleteAuthorized
            && requested is MoveJobEntryCleanupState.Deleted
                or MoveJobEntryCleanupState.Retained)
        {
            return requested;
        }

        throw new MoveNeedsAttentionException(
            $"The persisted move cleanup state cannot transition from {current} to {requested}.");
    }

    private static async Task<bool> IsLeaseActiveAsync(
        ListenArrDbContext db,
        Guid jobId,
        MoveLeaseToken leaseToken,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await db.MoveJobs.AnyAsync(
            job => job.Id == jobId
                && job.Status == MoveJobStatus.Running
                && job.LeaseOwner == leaseToken.Owner
                && job.LeaseGeneration == leaseToken.Generation
                && job.LeaseExpiresAt != null
                && job.LeaseExpiresAt > nowUtc,
            cancellationToken);

    private static void EnsureLeaseTokenProvided(
        Guid jobId,
        MoveLeaseToken leaseToken)
    {
        if (string.IsNullOrWhiteSpace(leaseToken.Owner)
            || leaseToken.Generation <= 0)
        {
            throw new MoveLeaseLostException(jobId, leaseToken.Generation);
        }
    }

    private static async Task ExecuteAsync(
        string operation,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (
            ShouldTranslate(exception, cancellationToken))
        {
            throw new PersistenceException($"Failed to {operation}.", exception);
        }
    }

    private static async Task<T> ExecuteAsync<T>(
        string operation,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (
            ShouldTranslate(exception, cancellationToken))
        {
            throw new PersistenceException($"Failed to {operation}.", exception);
        }
    }

    private static bool ShouldTranslate(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is PersistenceException
            or MoveLeaseLostException
            or MoveNeedsAttentionException)
        {
            return false;
        }

        if (exception is OperationCanceledException
            && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return ContainsProviderFailure(exception);
    }

    private static bool ContainsProviderFailure(Exception exception)
    {
        if (exception is DbException
            or DbUpdateException
            or DbUpdateConcurrencyException)
        {
            return true;
        }

        return exception.InnerException != null
            && ContainsProviderFailure(exception.InnerException);
    }
}
