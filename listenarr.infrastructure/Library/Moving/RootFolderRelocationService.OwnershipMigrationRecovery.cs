using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    internal Action? BeforeOwnershipMigrationMetadataSaveForTest
    {
        get;
        set;
    }

    internal Action? BeforeOwnershipMigrationAtomicCommitForTest
    {
        get;
        set;
    }

    internal Action? AfterOwnershipMigrationAtomicCommitForTest
    {
        get;
        set;
    }

    private async Task<List<RootFolderPathChangeResult>>
        ReconcileOwnershipPathMigrationsAsync(
            CancellationToken cancellationToken,
            Guid? requestedRelocationId = null)
    {
        await using var discoveryDb =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var relocationQuery = discoveryDb.RootFolderRelocations
            .AsNoTracking()
            .Where(relocation =>
                relocation.OwnershipPathMigrations.Count != 0);
        if (requestedRelocationId is { } requestedId)
        {
            relocationQuery = relocationQuery.Where(relocation =>
                relocation.Id == requestedId);
        }

        var relocationIds = await relocationQuery
            .OrderBy(relocation => relocation.CreatedAt)
            .ThenBy(relocation => relocation.Id)
            .Select(relocation => relocation.Id)
            .ToListAsync(cancellationToken);
        var results = new List<RootFolderPathChangeResult>();
        foreach (var relocationId in relocationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var db =
                await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var relocation = await db.RootFolderRelocations
                .AsSplitQuery()
                .Include(candidate => candidate.OwnershipPathMigrations)
                    .ThenInclude(migration => migration.Ownership)
                .Include(candidate => candidate.SkippedItems)
                .SingleAsync(
                    candidate => candidate.Id == relocationId,
                    cancellationToken);
            var plans = RehydrateOwnershipMigrationPlans(relocation);
            try
            {
                await CompleteOwnershipMigrationMetadataAsync(
                    db,
                    relocation,
                    plans,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException
                    or StackOverflowException))
            {
                db.ChangeTracker.Clear();
                var persistedRelocation = await db.RootFolderRelocations
                    .SingleAsync(
                        candidate => candidate.Id == relocationId,
                        CancellationToken.None);
                persistedRelocation.Status =
                    RootFolderRelocationStatus.Failed;
                persistedRelocation.Error =
                    $"{MetadataOnlyRecoveryAttentionPrefix}{exception.Message}";
                persistedRelocation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(CancellationToken.None);
            }

            var resultRelocation = await db.RootFolderRelocations
                .AsNoTracking()
                .Include(candidate => candidate.SkippedItems)
                .SingleAsync(
                    candidate => candidate.Id == relocationId,
                    CancellationToken.None);
            var currentPath = resultRelocation.RootFolderId is int rootId
                ? await db.RootFolders
                    .AsNoTracking()
                    .Where(root => root.Id == rootId)
                    .Select(root => root.Path)
                    .SingleOrDefaultAsync(CancellationToken.None)
                : null;
            results.Add(Map(
                resultRelocation,
                currentPath
                    ?? ResolveCurrentPathFallback(resultRelocation)));
        }

        return results;
    }

    private async Task CompleteOwnershipMigrationMetadataAsync(
        ListenArrDbContext db,
        RootFolderRelocation relocation,
        IReadOnlyList<OwnershipMigrationPlan> plans,
        CancellationToken cancellationToken)
    {
        var rootId = relocation.RootFolderId
            ?? throw new InvalidOperationException(
                "The ownership migration root no longer exists.");
        var root = await db.RootFolders.SingleOrDefaultAsync(
            candidate => candidate.Id == rootId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The ownership migration root no longer exists.");
        FileSystemPathSemantics sourceSemantics;
        FileSystemPathSemantics? journalTargetSemantics = null;
        if (plans.Count > 0)
        {
            sourceSemantics = new FileSystemPathSemantics(
                plans[0].Journal.SourcePathSyntax,
                plans[0].Journal.SourceCaseSensitivity);
            journalTargetSemantics = new FileSystemPathSemantics(
                plans[0].Journal.TargetPathSyntax,
                plans[0].Journal.TargetCaseSensitivity);
        }
        else if (!TryResolvePersistedRelocationSourceSemantics(
            relocation,
            out sourceSemantics,
            out var sourceReason))
        {
            throw new InvalidOperationException(sourceReason);
        }

        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                relocation.TargetPath,
                out var canonicalTargetPath,
                out var targetReason))
        {
            throw new InvalidOperationException(
                $"The relocation target is unavailable for ownership recovery: {targetReason}");
        }

        var targetResolution = await semanticsResolver.ResolveAsync(
            canonicalTargetPath,
            relocation.TargetCaseSensitivityMode,
            cancellationToken);
        if (targetResolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                targetResolution.Reason
                    ?? "The relocation target semantics are unavailable during metadata recovery.");
        }
        if (journalTargetSemantics.HasValue
            && targetResolution.Semantics != journalTargetSemantics.Value)
        {
            throw new InvalidOperationException(
                "The relocation target semantics changed before ownership recovery.");
        }
        var targetSemantics = journalTargetSemantics
            ?? targetResolution.Semantics;

        var audiobookRows = await db.Audiobooks
            .Where(audiobook => audiobook.BasePath != null)
            .Select(audiobook => new
            {
                Audiobook = audiobook,
                StoredBasePath = EF.Property<string>(
                    audiobook,
                    nameof(Audiobook.BasePath))!
            })
            .ToListAsync(cancellationToken);
        await db.AudiobookFiles.LoadAsync(cancellationToken);
        var candidates = audiobookRows
            .Select(row => new AudiobookPathCandidate(
                row.Audiobook,
                row.StoredBasePath))
            .ToList();
        var allowContextualAmbiguousSourceSyntax =
            !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                relocation.SourcePath,
                out _)
            && relocation.SourcePath.StartsWith("//", StringComparison.Ordinal)
            && FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                relocation.SourcePath,
                sourceSemantics.Syntax,
                out _);
        var (affected, invalid) = DiscoverAffectedAudiobooks(
            candidates,
            relocation.SourcePath,
            sourceSemantics,
            detectAmbiguousCaseMatches: false,
            allowContextualAmbiguousSourceSyntax);
        var alreadySkippedAudiobookIds = relocation.SkippedItems
            .Select(item => item.AudiobookId)
            .ToHashSet();
        var recoveryPlanning = PlanMetadataPathRewrites(
            db,
            affected
                .Where(candidate =>
                    !alreadySkippedAudiobookIds.Contains(candidate.Audiobook.Id))
                .ToArray(),
            relocation.SourcePath,
            relocation.TargetPath,
            sourceSemantics,
            targetSemantics,
            relocation.TargetCaseSensitivityMode,
            timeProvider.GetUtcNow());
        foreach (var skippedItem in recoveryPlanning.SkippedItems)
        {
            if (relocation.SkippedItems.All(item =>
                item.AudiobookId != skippedItem.AudiobookId))
            {
                relocation.SkippedItems.Add(skippedItem);
            }
        }
        foreach (var candidate in invalid)
        {
            if (relocation.SkippedItems.All(item =>
                item.AudiobookId != candidate.Audiobook.Id))
            {
                relocation.SkippedItems.Add(
                    new RootFolderRelocationSkippedItem
                    {
                        AudiobookId = candidate.Audiobook.Id,
                        Reason = EncodeMetadataSkipReason(
                            RootFolderRelocationSkipReasonCode.InvalidStoredPath,
                            InvalidStoredMetadataPathReason),
                        CreatedAt = timeProvider.GetUtcNow()
                    });
            }
        }
        var nonRepairableSkip = relocation.SkippedItems.FirstOrDefault(item =>
            !IsRepairableMetadataSkipReason(
                ClassifyMetadataSkipReason(item.Reason)));
        if (nonRepairableSkip != null)
        {
            throw new InvalidOperationException(
                $"Metadata-only recovery cannot safely publish a partial repair for audiobook {nonRepairableSkip.AudiobookId}: {nonRepairableSkip.Reason}");
        }

        var ownershipPreparation = await RevalidateRecoveredOwnershipPlansAsync(
            db,
            root,
            plans,
            relocation.SkippedItems.Select(item => item.AudiobookId).ToHashSet(),
            cancellationToken);
        var transferPlans = ownershipPreparation.Transfers;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? targetGenerationLease = null;
        if (relocation.TargetDirectoryObjectIdentityVersion.HasValue
            && !string.IsNullOrWhiteSpace(relocation.TargetDirectoryObjectIdentity)
            && string.IsNullOrWhiteSpace(
                relocation.TargetDirectoryObjectIdentityUnavailableReason))
        {
            var currentTargetGeneration =
                await ResolveExistingDirectoryObjectIdentityAsync(
                    relocation.TargetPath,
                    relocation.TargetDirectoryObjectIdentityVersion.Value,
                    relocation.TargetDirectoryObjectIdentity!,
                    cancellationToken);
            if (currentTargetGeneration.IsAvailable)
            {
                targetGenerationLease = PinTargetDirectoryGeneration(
                    relocation.TargetPath,
                    relocation.TargetDirectoryObjectIdentityVersion,
                    relocation.TargetDirectoryObjectIdentity,
                    relocation.TargetDirectoryObjectIdentityUnavailableReason,
                    cancellationToken);
            }
            else
            {
                // Metadata-only repair does not require physical authority over the
                // target generation. If that generation changed while the metadata
                // saga was incomplete, drop the stale authority and require a later
                // explicit root confirmation rather than adopting the replacement.
                relocation.TargetDirectoryObjectIdentityVersion = null;
                relocation.TargetDirectoryObjectIdentity = null;
                relocation.TargetDirectoryObjectIdentityUnavailableReason =
                    "The root folder directory changed during metadata repair and must be confirmed before filesystem mutations.";
                relocation.TargetIdentityEnrollmentState =
                    TargetIdentityEnrollmentState.Unavailable;
            }
        }
        IReadOnlyList<OwnershipMigrationTargetLease> ownershipGenerationLeases = [];
        try
        {
            ownershipGenerationLeases = PinOwnershipMigrationTargets(
                transferPlans,
                relocation.TargetPath,
                cancellationToken);
            await using var transaction =
                await db.Database.BeginTransactionAsync(cancellationToken);
            foreach (var plan in recoveryPlanning.SafePlans)
            {
                AudiobookPathReferenceRewriter.Rewrite(
                    plan.Candidate.Audiobook,
                    plan.Candidate.StoredBasePath,
                    plan.Destination,
                    sourceSemantics,
                    targetSemantics,
                    relocation.TargetCaseSensitivityMode);
            }
            RejectDuplicateAudiobookFileOwnership(db);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            ApplyOwnershipMigrationMetadata(transferPlans, now);
            RetireUntransferredOwnerships(
                ownershipPreparation.Retirements,
                now);
            BeforeOwnershipMigrationMetadataSaveForTest?.Invoke();
            await db.SaveChangesAsync(cancellationToken);
            AssignOwnershipMigrationKeys(
                transferPlans,
                now);
            var command = new RootFolderPathChangeCommand(
                relocation.TargetPath,
                relocation.Mode,
                relocation.DeleteEmptySource,
                relocation.DesiredName,
                relocation.DesiredIsDefault,
                relocation.TargetCaseSensitivityMode);
            ApplyRootMetadata(
                root,
                command,
                relocation.TargetPath,
                targetResolution,
                FileSystemPathIdentity.CreateKey(
                    "root",
                    relocation.TargetPath,
                    targetSemantics));
            if (relocation.DesiredIsDefault)
            {
                await ClearOtherDefaultsAsync(
                    db,
                    rootId,
                    cancellationToken);
            }
            root.DirectoryObjectIdentityVersion =
                relocation.TargetDirectoryObjectIdentityVersion;
            root.DirectoryObjectIdentity =
                relocation.TargetDirectoryObjectIdentity;
            root.DirectoryObjectIdentityUnavailableReason =
                relocation.TargetDirectoryObjectIdentityUnavailableReason;
            relocation.CompletedJobs = Math.Max(
                0,
                relocation.TotalJobs - relocation.SkippedItems.Count);
            relocation.Status = relocation.SkippedItems.Count == 0
                ? RootFolderRelocationStatus.Completed
                : RootFolderRelocationStatus.NeedsAttention;
            relocation.ActiveRootFolderId =
                relocation.SkippedItems.Count == 0 ? null : root.Id;
            relocation.CompletedAt =
                relocation.SkippedItems.Count == 0 ? now : null;
            relocation.Error = relocation.SkippedItems.Count == 0
                ? null
                : BuildSkippedMetadataError(
                    relocation.SkippedItems.Count);
            relocation.TargetIdentityEnrollmentState =
                relocation.SkippedItems.Count == 0
                    ? TargetIdentityEnrollmentState.NotRequired
                    : relocation.TargetIdentityEnrollmentState;
            relocation.UpdatedAt = now;
            db.LibraryDirectoryOwnershipPathMigrations.RemoveRange(
                plans.Select(plan => plan.Journal));
            await db.SaveChangesAsync(cancellationToken);
            BeforeOwnershipMigrationAtomicCommitForTest?.Invoke();
            if (targetGenerationLease != null)
            {
                RevalidatePinnedTargetDirectoryGeneration(
                    targetGenerationLease,
                    relocation.TargetDirectoryObjectIdentityVersion,
                    relocation.TargetDirectoryObjectIdentity,
                    relocation.TargetDirectoryObjectIdentityUnavailableReason,
                    cancellationToken);
            }
            RevalidateOwnershipMigrationTargetLeases(
                ownershipGenerationLeases,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            AfterOwnershipMigrationAtomicCommitForTest?.Invoke();
            if (targetGenerationLease != null)
            {
                RevalidatePinnedTargetDirectoryGeneration(
                    targetGenerationLease,
                    relocation.TargetDirectoryObjectIdentityVersion,
                    relocation.TargetDirectoryObjectIdentity,
                    relocation.TargetDirectoryObjectIdentityUnavailableReason,
                    CancellationToken.None);
            }
            RevalidateOwnershipMigrationTargetLeases(
                ownershipGenerationLeases,
                CancellationToken.None);
        }
        finally
        {
            DisposeOwnershipMigrationTargetLeases(ownershipGenerationLeases);
            targetGenerationLease?.Dispose();
        }
    }

    private static IReadOnlyList<OwnershipMigrationPlan>
        RehydrateOwnershipMigrationPlans(
            RootFolderRelocation relocation)
    {
        var plans = new List<OwnershipMigrationPlan>();
        foreach (var journal in relocation.OwnershipPathMigrations)
        {
            var tracked = journal.Ownership;
            var source = SnapshotOwnership(tracked);
            source.Path = journal.SourceCanonicalPath;
            source.CanonicalPath = journal.SourceCanonicalPath;
            source.PathSyntax = journal.SourcePathSyntax;
            source.PathCaseSensitivity =
                journal.SourceCaseSensitivity;
            source.PathCaseSensitivityMode =
                journal.SourceCaseSensitivityMode;
            source.PathIdentityBoundary =
                journal.SourceIdentityBoundary;
            source.PathIdentityLookupKey =
                journal.SourceIdentityLookupKey;
            source.PathOwnershipKey =
                journal.SourceOwnershipKey;
            source.ManagedRootFolderId = relocation.RootFolderId;

            var target = SnapshotOwnership(tracked);
            target.Path = journal.TargetCanonicalPath;
            target.CanonicalPath = journal.TargetCanonicalPath;
            target.PathSyntax = journal.TargetPathSyntax;
            target.PathCaseSensitivity =
                journal.TargetCaseSensitivity;
            target.PathCaseSensitivityMode =
                journal.TargetCaseSensitivityMode;
            target.PathIdentityBoundary =
                journal.TargetIdentityBoundary;
            target.PathIdentityLookupKey =
                journal.TargetIdentityLookupKey;
            target.PathOwnershipKey =
                journal.TargetOwnershipKey;
            target.ManagedRootFolderId = relocation.RootFolderId;
            plans.Add(new OwnershipMigrationPlan(
                tracked,
                source,
                target,
                journal));
        }

        return plans;
    }
}
