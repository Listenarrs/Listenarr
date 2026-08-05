using System.Data.Common;
using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore
{
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
            await ManagedDirectoryEnrollment.RequireMatchingEnrollmentAsync(
                root,
                relocation.TargetDirectoryObjectIdentityVersion,
                relocation.TargetDirectoryObjectIdentity,
                relocation.TargetDirectoryObjectIdentityUnavailableReason,
                cancellationToken);
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
            var nativeIdentity = boundary.GetDirectoryObjectIdentity();
            var current = await ManagedDirectoryEnrollment.ResolveAsync(
                boundary,
                nativeIdentity,
                enrollIfMissing: false,
                cancellationToken);
            var currentVersion = (int)authorizationEntries[0].Length;
            if (!current.IsAvailable
                || current.Version != currentVersion
                || !string.Equals(
                    MoveManifestIdentity.ComputeTargetBoundaryAuthorizationDigest(
                        currentVersion,
                        current.Value!),
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
