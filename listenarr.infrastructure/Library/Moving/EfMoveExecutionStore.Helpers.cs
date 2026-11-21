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
        if (resolution.State == PathIdentityState.Unavailable)
        {
            throw new IOException(
                resolution.Reason
                    ?? $"The move {description} filesystem semantics are temporarily unavailable.");
        }
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
                or MoveJobEntryCleanupState.Retained or MoveJobEntryCleanupState.Deleted)
        {
            // Deleted can record verified source absence after restart. It never
            // grants authority to delete a pathname or skip live verification.
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
