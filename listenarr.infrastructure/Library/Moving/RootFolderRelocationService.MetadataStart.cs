using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    internal Action? AfterMetadataOnlyJournalCommitForTest
    {
        get;
        set;
    }

    internal Action? BeforeMetadataOnlyAtomicCommitForTest
    {
        get;
        set;
    }

    internal Action? AfterMetadataOnlyAtomicCommitForTest
    {
        get;
        set;
    }

    private async Task<StartOutcome> StartMetadataOnlyAsync(
        ListenArrDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        RootFolder root,
        RootFolderPathChangeCommand command,
        string targetPath,
        FileSystemSemanticsResolution targetResolution,
        DirectoryObjectIdentityResolution targetObjectIdentity,
        string targetIdentityKey,
        FileSystemCaseSensitivityMode sourceCaseSensitivityMode,
        IReadOnlyList<AudiobookPathCandidate> affected,
        IReadOnlyList<AudiobookPathCandidate> invalidStoredBasePaths,
        FileSystemPathSemantics? metadataSourceSemantics,
        FileSystemPathSemantics? ownershipSourceSemantics,
        int rootFolderId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowUtc = now.UtcDateTime;
        var sourcePath = root.Path;
        var skipped = invalidStoredBasePaths
            .Select(candidate => new RootFolderRelocationSkippedItem
            {
                AudiobookId = candidate.Audiobook.Id,
                Reason = EncodeMetadataSkipReason(
                    RootFolderRelocationSkipReasonCode.InvalidStoredPath,
                    InvalidStoredMetadataPathReason),
                CreatedAt = now
            })
            .ToList();
        var metadataTotal = affected.Count + skipped.Count;
        var completed = 0;
        var metadataPlanning = PlanMetadataPathRewrites(
            db,
            affected,
            sourcePath,
            targetPath,
            metadataSourceSemantics,
            targetResolution.Semantics,
            command.TargetCaseSensitivityMode,
            now);
        var metadataPlans = metadataPlanning.SafePlans;
        skipped.AddRange(metadataPlanning.SkippedItems);
        var nonRepairableSkip = skipped.FirstOrDefault(item =>
            !IsRepairableMetadataSkipReason(
                ClassifyMetadataSkipReason(item.Reason)));
        if (nonRepairableSkip != null)
        {
            throw new RootFolderPathChangeRejectedException(
                "root_folder_metadata_path_repair_required",
                "One or more audiobooks under this root have stored paths that cannot be rebased safely to the selected destination. Repair those audiobook paths or choose a compatible destination before changing this root folder.",
                $"Metadata-only relocation cannot safely publish a partial repair for audiobook {nonRepairableSkip.AudiobookId}: {nonRepairableSkip.Reason}");
        }

        var metadataRelocation = new RootFolderRelocation
        {
            RootFolderId = root.Id,
            ActiveRootFolderId = root.Id,
            SourcePath = sourcePath,
            SourceCaseSensitivityMode = sourceCaseSensitivityMode,
            TargetPath = targetPath,
            TargetDirectoryObjectIdentityVersion =
                targetObjectIdentity.Version,
            TargetDirectoryObjectIdentity =
                targetObjectIdentity.Value,
            TargetDirectoryObjectIdentityUnavailableReason =
                targetObjectIdentity.UnavailableReason,
            TargetIdentityEnrollmentState =
                targetObjectIdentity.IsAvailable
                    ? TargetIdentityEnrollmentState.Authorized
                    : TargetIdentityEnrollmentState.Unavailable,
            Mode = command.Mode,
            Status = RootFolderRelocationStatus.Pending,
            DeleteEmptySource = command.DeleteEmptySource,
            DesiredName = command.DesiredName.Trim(),
            DesiredIsDefault = command.DesiredIsDefault,
            TargetCaseSensitivityMode =
                command.TargetCaseSensitivityMode,
            TotalJobs = metadataTotal,
            CompletedJobs = 0,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };
        foreach (var skippedItem in skipped)
        {
            metadataRelocation.SkippedItems.Add(skippedItem);
        }
        db.RootFolderRelocations.Add(metadataRelocation);

        var ownershipPreparation = await PrepareOwnershipMigrationsAsync(
            db,
            metadataRelocation,
            root,
            ownershipSourceSemantics,
            targetResolution.Semantics,
            skipped.Select(item => item.AudiobookId).ToHashSet(),
            cancellationToken);
        var ownershipPlans = ownershipPreparation.Transfers;
        await db.SaveChangesAsync(cancellationToken);
        var completionToken = RequestCancellationBoundary.EnterNonCancelablePhase(
            cancellationToken);
        await transaction.CommitAsync(completionToken);
        AfterMetadataOnlyJournalCommitForTest?.Invoke();
        PinnedDirectoryCreation.PinnedDirectoryAnchor? targetGenerationLease = null;
        IReadOnlyList<OwnershipMigrationTargetLease> ownershipGenerationLeases = [];
        try
        {
            if (targetObjectIdentity.IsAvailable)
            {
                targetGenerationLease = PinTargetDirectoryGeneration(
                    targetPath,
                    targetObjectIdentity.Version,
                    targetObjectIdentity.Value,
                    targetObjectIdentity.UnavailableReason,
                    completionToken);
            }
            ownershipGenerationLeases = PinOwnershipMigrationTargets(
                ownershipPlans,
                targetPath,
                completionToken);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            DisposeOwnershipMigrationTargetLeases(ownershipGenerationLeases);
            targetGenerationLease?.Dispose();
            metadataRelocation.Status =
                RootFolderRelocationStatus.Failed;
            metadataRelocation.Error =
                $"{MetadataOnlyTargetVerificationAttentionPrefix}{exception.Message}";
            metadataRelocation.UpdatedAt =
                timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(completionToken);
            throw;
        }

        try
        {
            await using var metadataTransaction =
                await db.Database.BeginTransactionAsync(completionToken);
            foreach (var plan in metadataPlans)
            {
                AudiobookPathReferenceRewriter.Rewrite(
                    plan.Candidate.Audiobook,
                    plan.Candidate.StoredBasePath,
                    plan.Destination,
                    metadataSourceSemantics!.Value,
                    targetResolution.Semantics,
                    command.TargetCaseSensitivityMode);
                completed++;
            }

            RejectDuplicateAudiobookFileOwnership(db);
            ApplyOwnershipMigrationMetadata(ownershipPlans, nowUtc);
            RetireUntransferredOwnerships(
                ownershipPreparation.Retirements,
                nowUtc);
            await db.SaveChangesAsync(completionToken);
            AssignOwnershipMigrationKeys(
                ownershipPlans,
                nowUtc);
            ApplyRootMetadata(
                root,
                command,
                targetPath,
                targetResolution,
                targetIdentityKey);
            ApplyRootDirectoryObjectIdentity(root, targetObjectIdentity);
            if (command.DesiredIsDefault)
            {
                await ClearOtherDefaultsAsync(
                    db,
                    rootFolderId,
                    completionToken);
            }

            metadataRelocation.CompletedJobs = completed;
            metadataRelocation.Status = skipped.Count > 0
                ? RootFolderRelocationStatus.NeedsAttention
                : RootFolderRelocationStatus.Completed;
            metadataRelocation.ActiveRootFolderId =
                skipped.Count > 0 ? root.Id : null;
            metadataRelocation.CompletedAt =
                skipped.Count > 0 ? null : nowUtc;
            metadataRelocation.Error = skipped.Count > 0
                ? BuildSkippedMetadataError(skipped.Count)
                : null;
            metadataRelocation.TargetIdentityEnrollmentState =
                skipped.Count > 0
                    ? metadataRelocation.TargetIdentityEnrollmentState
                    : TargetIdentityEnrollmentState.NotRequired;
            metadataRelocation.UpdatedAt = nowUtc;
            db.LibraryDirectoryOwnershipPathMigrations.RemoveRange(
                ownershipPlans.Select(plan => plan.Journal));
            var completedWithoutAttention = skipped.Count == 0;
            var metadataResult = new RootFolderPathChangeResult(
                completedWithoutAttention ? null : metadataRelocation.Id,
                root.Id,
                targetPath,
                targetPath,
                metadataRelocation.Status,
                metadataTotal,
                completed,
                metadataRelocation.Error,
                metadataRelocation.TargetIdentityEnrollmentState,
                skipped
                    .Select(item => item.AudiobookId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray(),
                RootFolderRelocationMode.MetadataOnly,
                skipped
                    .OrderBy(item => item.AudiobookId)
                    .Select(item => new RootFolderRelocationSkippedItemResult(
                        item.AudiobookId,
                        ClassifyMetadataSkipReason(item.Reason)))
                    .ToArray());
            await db.SaveChangesAsync(completionToken);
            BeforeMetadataOnlyAtomicCommitForTest?.Invoke();
            if (targetGenerationLease != null)
            {
                RevalidatePinnedTargetDirectoryGeneration(
                    targetGenerationLease,
                    targetObjectIdentity.Version,
                    targetObjectIdentity.Value,
                    targetObjectIdentity.UnavailableReason,
                    completionToken);
            }
            RevalidateOwnershipMigrationTargetLeases(
                ownershipGenerationLeases,
                completionToken);
            await metadataTransaction.CommitAsync(CancellationToken.None);
            AfterMetadataOnlyAtomicCommitForTest?.Invoke();
            if (targetGenerationLease != null)
            {
                RevalidatePinnedTargetDirectoryGeneration(
                    targetGenerationLease,
                    targetObjectIdentity.Version,
                    targetObjectIdentity.Value,
                    targetObjectIdentity.UnavailableReason,
                    CancellationToken.None);
            }
            RevalidateOwnershipMigrationTargetLeases(
                ownershipGenerationLeases,
                CancellationToken.None);
            if (completedWithoutAttention)
            {
                db.RootFolderRelocations.Remove(metadataRelocation);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            return new StartOutcome(metadataResult, true);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            db.ChangeTracker.Clear();
            var persistedRelocation = await db.RootFolderRelocations
                .SingleAsync(
                    candidate => candidate.Id == metadataRelocation.Id,
                    CancellationToken.None);
            persistedRelocation.Status = RootFolderRelocationStatus.Failed;
            persistedRelocation.ActiveRootFolderId = rootFolderId;
            persistedRelocation.CompletedAt = null;
            persistedRelocation.Error =
                $"{MetadataOnlyCompletionAttentionPrefix}{exception.Message}";
            persistedRelocation.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            DisposeOwnershipMigrationTargetLeases(ownershipGenerationLeases);
            targetGenerationLease?.Dispose();
        }
    }
}
