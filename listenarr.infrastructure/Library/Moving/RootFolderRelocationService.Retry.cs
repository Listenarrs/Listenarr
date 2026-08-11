using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    public async Task<RootFolderPathChangeResult> RetryAsync(
        Guid relocationId,
        CancellationToken cancellationToken = default)
    {
        var result = await _mutationCoordinator.ExecuteExclusiveAsync(
            token => ExecuteWithAllAudiobookLocksAsync(
                lockedToken => RetryWithOwnershipRecoveryAsync(
                    relocationId,
                    lockedToken),
                token),
            cancellationToken);
        await BroadcastAsync(result, cancellationToken);
        return result;
    }

    private async Task<RootFolderPathChangeResult>
        RetryWithOwnershipRecoveryAsync(
            Guid relocationId,
            CancellationToken cancellationToken)
    {
        bool hasOwnershipMigration;
        bool requiresMetadataCompletionRecovery;
        await using (var preflight =
            await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var state = await preflight.RootFolderRelocations
                .AsNoTracking()
                .Where(candidate => candidate.Id == relocationId)
                .Select(candidate => new
                {
                    candidate.Status,
                    candidate.Mode,
                    HasOwnershipMigration =
                        candidate.OwnershipPathMigrations.Count != 0
                })
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException(
                    "Root folder relocation not found");
            var retryableAttention =
                state.Status == RootFolderRelocationStatus.NeedsAttention;
            var retryableMetadataFailure =
                state.Mode == RootFolderRelocationMode.MetadataOnly
                && state.Status == RootFolderRelocationStatus.Failed;
            if (!retryableAttention && !retryableMetadataFailure)
            {
                throw new InvalidOperationException(
                    "Only relocations needing attention or failed metadata repairs can be retried.");
            }

            if (state.Mode == RootFolderRelocationMode.MetadataOnly)
            {
                _filesystemReadiness.EnsureMetadataRepairReady();
            }
            else
            {
                EnsureFilesystemMutationReady();
            }

            hasOwnershipMigration = state.HasOwnershipMigration;
            requiresMetadataCompletionRecovery =
                state.Mode == RootFolderRelocationMode.MetadataOnly
                && !hasOwnershipMigration
                && state.Status == RootFolderRelocationStatus.Failed;
        }

        if (requiresMetadataCompletionRecovery)
        {
            return await RecoverCommittedMetadataOnlyRelocationAsync(
                relocationId,
                cancellationToken);
        }

        if (!hasOwnershipMigration)
        {
            return await RetryCoreAsync(
                relocationId,
                cancellationToken);
        }

        var recovered = await ReconcileOwnershipPathMigrationsAsync(
            cancellationToken,
            relocationId);
        return recovered.SingleOrDefault()
            ?? throw new InvalidOperationException(
                "The ownership migration recovery journal disappeared before retry.");
    }

    private async Task<RootFolderPathChangeResult> RetryCoreAsync(
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var relocation = await db.RootFolderRelocations
            .AsSplitQuery()
            .Include(candidate => candidate.MoveJobs)
                .ThenInclude(job => job.Entries)
            .Include(candidate => candidate.SkippedItems)
            .SingleOrDefaultAsync(candidate => candidate.Id == relocationId, cancellationToken)
            ?? throw new KeyNotFoundException("Root folder relocation not found");
        if (relocation.Status != RootFolderRelocationStatus.NeedsAttention)
        {
            throw new InvalidOperationException("Only relocations needing attention can be retried.");
        }
        if (relocation.Mode == RootFolderRelocationMode.Relocate
            && relocation.TargetIdentityEnrollmentState
                == TargetIdentityEnrollmentState.Unavailable)
        {
            throw new InvalidOperationException(
                "The relocation target identity is unavailable and cannot be retried safely.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var needsTargetSemantics = relocation.SkippedItems.Count > 0;
        FileSystemSemanticsResolution? targetResolution = null;
        if (needsTargetSemantics)
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    relocation.TargetPath,
                    out var canonicalTargetPath,
                    out var targetPathReason))
            {
                relocation.Status = RootFolderRelocationStatus.NeedsAttention;
                relocation.Error = targetPathReason;
                relocation.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
                var fallbackPath = ResolveCurrentPathFallback(relocation);
                var unavailableResult = Map(relocation, fallbackPath);
                return unavailableResult;
            }

            targetResolution = await semanticsResolver.ResolveAsync(
                canonicalTargetPath,
                relocation.TargetCaseSensitivityMode,
                cancellationToken);
            if (targetResolution.State != PathIdentityState.Valid)
            {
                relocation.Status = RootFolderRelocationStatus.NeedsAttention;
                relocation.Error = targetResolution.Reason ?? "Target filesystem identity is unavailable.";
                relocation.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
                var fallbackPath = ResolveCurrentPathFallback(relocation);
                string? unavailableRootPath = null;
                if (relocation.RootFolderId is int unavailableRootFolderId)
                {
                    unavailableRootPath = await db.RootFolders
                        .Where(root => root.Id == unavailableRootFolderId)
                        .Select(root => root.Path)
                        .SingleOrDefaultAsync(CancellationToken.None);
                }

                var unavailableResult = Map(relocation, unavailableRootPath ?? fallbackPath);
                return unavailableResult;
            }

            if (relocation.Mode == RootFolderRelocationMode.MetadataOnly
                && relocation.RootFolderId is int metadataRootId)
            {
                var repairedRoot = await db.RootFolders
                    .SingleOrDefaultAsync(
                        root => root.Id == metadataRootId,
                        cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The root folder no longer exists; this metadata repair cannot be retried.");
                var persistedTargetSemantics =
                    RootFolderPathSemantics.ResolvePersisted(repairedRoot)?.Semantics;
                if (!persistedTargetSemantics.HasValue
                    || persistedTargetSemantics.Value != targetResolution.Semantics)
                {
                    relocation.Status = RootFolderRelocationStatus.NeedsAttention;
                    relocation.Error =
                        "The target filesystem case semantics changed after the root repair. Confirm or repair the root folder before retrying skipped audiobooks.";
                    relocation.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await transaction.CommitAsync(CancellationToken.None);
                    return Map(relocation, repairedRoot.Path);
                }
            }
        }

        var skippedSupersededJobs = 0;
        var unsafeRetryJobs = 0;
        foreach (var job in relocation.MoveJobs.Where(job => job.Status is
            MoveJobStatus.NeedsAttention or MoveJobStatus.Failed or MoveJobStatus.Superseded))
        {
            // Superseded jobs are terminal evidence that their persisted source snapshot is
            // stale. Retrying them would reactivate the exact unsafe operation that superseded
            // status is intended to fence off.
            if (job.Status == MoveJobStatus.Superseded)
            {
                skippedSupersededJobs++;
                continue;
            }

            string? sourceIdentityError = null;
            if (string.IsNullOrWhiteSpace(job.SourcePath)
                || !job.TryGetSourceIdentity(out var sourceIdentity)
                || !TryValidateRetryIdentity(
                    sourceIdentity,
                    job.SourcePath,
                    out sourceIdentityError))
            {
                job.Status = MoveJobStatus.NeedsAttention;
                job.Error = sourceIdentityError
                    ?? "The move job has no authoritative source filesystem identity.";
                job.FailureKind = MoveFailureKind.Verification;
                job.ActiveDeduplicationKey = null;
                unsafeRetryJobs++;
                continue;
            }

            string? targetIdentityError = null;
            if (string.IsNullOrWhiteSpace(job.RequestedPath)
                || !job.TryGetTargetIdentity(out var targetIdentity)
                || !TryValidateRetryIdentity(
                    targetIdentity,
                    job.RequestedPath,
                    out targetIdentityError))
            {
                job.Status = MoveJobStatus.NeedsAttention;
                job.Error = targetIdentityError
                    ?? "The move job has no authoritative target filesystem identity.";
                job.FailureKind = MoveFailureKind.Verification;
                job.ActiveDeduplicationKey = null;
                unsafeRetryJobs++;
                continue;
            }

            if (job.Entries.Count == 0
                || job.Entries.All(entry => entry.EntryType != MoveJobEntryType.File))
            {
                job.Status = MoveJobStatus.NeedsAttention;
                job.Error = "The move job has no persisted tracked-file source manifest and cannot be retried safely.";
                job.FailureKind = MoveFailureKind.Verification;
                job.ActiveDeduplicationKey = null;
                unsafeRetryJobs++;
                continue;
            }
            if (!MoveManifestIdentity.TryGetTargetBoundaryAuthorization(
                    job.Entries,
                    out _,
                    out _))
            {
                job.Status = MoveJobStatus.NeedsAttention;
                job.Error = "The move job has no durable target-boundary physical-generation authorization and cannot be retried safely.";
                job.FailureKind = MoveFailureKind.Verification;
                job.ActiveDeduplicationKey = null;
                unsafeRetryJobs++;
                continue;
            }

            var deduplicationKey = MoveManifestIdentity.CreateDeduplicationKey(
                job.AudiobookId,
                job.SourcePath,
                sourceIdentity,
                job.RequestedPath,
                targetIdentity,
                job.Entries);
            var conflictingJob = await db.MoveJobs.AsNoTracking().FirstOrDefaultAsync(
                candidate => candidate.Id != job.Id
                    && candidate.ActiveDeduplicationKey == deduplicationKey,
                cancellationToken);
            if (conflictingJob != null)
            {
                throw new ApplicationConflictException(
                    "move_job_retry_conflict",
                    "A newer move for this audiobook is already active.");
            }

            MoveJobManualRetry.Reset(job, deduplicationKey, now);
            job.IdentityKeyVersion = MoveManifestIdentity.Version;
        }

        if (relocation.SkippedItems.Count > 0)
        {
            await RetrySkippedMetadataReferencesAsync(
                db,
                relocation,
                targetResolution!.Semantics,
                cancellationToken);
        }

        var remainingSkippedItems = relocation.SkippedItems.Count;
        if (remainingSkippedItems > 0
            || skippedSupersededJobs > 0
            || unsafeRetryJobs > 0)
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            var retryError = BuildRetryAttentionError(
                remainingSkippedItems,
                skippedSupersededJobs);
            var unsafeError = unsafeRetryJobs > 0
                ? $"{unsafeRetryJobs} job(s) lacked authoritative source identity or tracked-file manifest evidence and were not retried."
                : string.Empty;
            relocation.Error = string.Join(
                ' ',
                new[] { retryError, unsafeError }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
        }
        else if (relocation.MoveJobs.Count == 0)
        {
            if (relocation.Mode == RootFolderRelocationMode.Relocate
                && relocation.TotalJobs > 0)
            {
                throw new InvalidOperationException(
                    "The relocation was interrupted before its persisted move jobs were published and cannot be retried automatically.");
            }
            if (relocation.RootFolderId is not int emptyRootFolderId)
            {
                throw new InvalidOperationException(
                    "The root folder no longer exists; this relocation cannot be retried.");
            }
            var emptyRoot = await db.RootFolders.SingleOrDefaultAsync(
                candidate => candidate.Id == emptyRootFolderId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The root folder no longer exists; this relocation cannot be retried.");
            if (relocation.Mode == RootFolderRelocationMode.Relocate)
            {
                await FinalizeCompletedRelocationAsync(
                    db,
                    relocation,
                    emptyRoot,
                    now,
                    cancellationToken);
            }
            else
            {
                relocation.Status =
                    RootFolderRelocationStatus.Completed;
                relocation.ActiveRootFolderId = null;
                relocation.CompletedAt = now;
                relocation.Error = null;
            }
            relocation.CompletedJobs = relocation.TotalJobs;
            relocation.TargetIdentityEnrollmentState =
                TargetIdentityEnrollmentState.NotRequired;
        }
        else if (relocation.MoveJobs.All(job => job.Status == MoveJobStatus.Completed))
        {
            if (relocation.RootFolderId is not int rootFolderId)
            {
                throw new InvalidOperationException(
                    "The root folder no longer exists; this relocation cannot be retried.");
            }

            var root = await db.RootFolders.SingleOrDefaultAsync(
                candidate => candidate.Id == rootFolderId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The root folder no longer exists; this relocation cannot be retried.");
            await FinalizeCompletedRelocationAsync(
                db,
                relocation,
                root,
                now,
                cancellationToken);
            relocation.CompletedJobs = relocation.TotalJobs;
        }
        else
        {
            relocation.Status = RootFolderRelocationStatus.Running;
            relocation.Error = null;
        }

        relocation.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        var resultFallbackPath = ResolveCurrentPathFallback(relocation);
        string? rootPath = null;
        if (relocation.RootFolderId is int resultRootFolderId)
        {
            rootPath = await db.RootFolders
                .Where(root => root.Id == resultRootFolderId)
                .Select(root => root.Path)
                .SingleOrDefaultAsync(CancellationToken.None);
        }

        var result = Map(relocation, rootPath ?? resultFallbackPath);
        return result;
    }

    private static bool TryValidateRetryIdentity(
        PathIdentitySnapshot identity,
        string path,
        out string? error)
    {
        try
        {
            if (!FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                    path,
                    identity,
                    out _,
                    out var reason))
            {
                error = $"The move job has an invalid persisted filesystem identity: {reason}";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
            or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            error = $"The move job has an invalid persisted filesystem identity: {exception.Message}";
            return false;
        }
    }
}
