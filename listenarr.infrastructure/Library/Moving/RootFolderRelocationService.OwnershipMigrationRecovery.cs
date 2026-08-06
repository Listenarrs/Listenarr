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
                .Include(candidate => candidate.OwnershipPathMigrations)
                    .ThenInclude(migration => migration.Ownership)
                .Include(candidate => candidate.SkippedItems)
                .SingleAsync(
                    candidate => candidate.Id == relocationId,
                    cancellationToken);
            var plans = RehydrateOwnershipMigrationPlans(relocation);
            try
            {
                await RequireTargetDirectoryGenerationAsync(
                    relocation.TargetPath,
                    relocation.TargetDirectoryObjectIdentityVersion,
                    relocation.TargetDirectoryObjectIdentity,
                    relocation.TargetDirectoryObjectIdentityUnavailableReason,
                    cancellationToken);
                var preparedPlans = plans
                    .Where(plan => plan.Journal.State
                        == LibraryDirectoryOwnershipPathMigrationState.Prepared)
                    .ToList();
                if (preparedPlans.Count > 0)
                {
                    await PublishOwnershipMigrationTargetsAsync(
                        preparedPlans,
                        relocation.TargetPath,
                        cancellationToken);
                    foreach (var plan in preparedPlans)
                    {
                        plan.Journal.State =
                            LibraryDirectoryOwnershipPathMigrationState
                                .MarkersPublished;
                        plan.Journal.UpdatedAt =
                            timeProvider.GetUtcNow().UtcDateTime;
                    }
                    await db.SaveChangesAsync(cancellationToken);
                }

                var publishedPlans = plans
                    .Where(plan => plan.Journal.State
                        == LibraryDirectoryOwnershipPathMigrationState
                            .MarkersPublished)
                    .ToList();
                if (publishedPlans.Count > 0)
                {
                    // Re-prove the target directory generation and both durable
                    // markers after every restart before committing metadata.
                    await PublishOwnershipMigrationTargetsAsync(
                        publishedPlans,
                        relocation.TargetPath,
                        cancellationToken,
                        allowPublication: false);
                    await CompleteOwnershipMigrationMetadataAsync(
                        db,
                        relocation,
                        plans,
                        cancellationToken);
                }

                if (plans.All(plan =>
                    plan.Journal.State
                        == LibraryDirectoryOwnershipPathMigrationState
                            .MetadataCommitted))
                {
                    await PublishOwnershipMigrationTargetsAsync(
                        plans,
                        relocation.TargetPath,
                        CancellationToken.None,
                        allowPublication: false);
                    await RetireOwnershipMigrationSourcesAsync(
                        plans,
                        relocation.SourcePath,
                        relocation.TargetPath,
                        relocation.TargetDirectoryObjectIdentityVersion,
                        relocation.TargetDirectoryObjectIdentity,
                        relocation.TargetDirectoryObjectIdentityUnavailableReason,
                        CancellationToken.None);
                    foreach (var plan in plans)
                    {
                        plan.Journal.State =
                            LibraryDirectoryOwnershipPathMigrationState.SourceMarkersRetired;
                        plan.Journal.UpdatedAt =
                            timeProvider.GetUtcNow().UtcDateTime;
                    }
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                if (plans.All(plan =>
                    plan.Journal.State
                        == LibraryDirectoryOwnershipPathMigrationState
                            .SourceMarkersRetired))
                {
                    await RetireOwnershipMigrationTargetsAsync(
                        plans,
                        relocation.TargetPath,
                        relocation.TargetDirectoryObjectIdentityVersion,
                        relocation.TargetDirectoryObjectIdentity,
                        relocation.TargetDirectoryObjectIdentityUnavailableReason,
                        CancellationToken.None);
                    db.LibraryDirectoryOwnershipPathMigrations
                        .RemoveRange(plans.Select(plan => plan.Journal));
                    FinalizeRecoveredMetadataOnlyRelocation(
                        relocation,
                        timeProvider.GetUtcNow().UtcDateTime);
                    await db.SaveChangesAsync(CancellationToken.None);
                }
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
                    RootFolderRelocationStatus.NeedsAttention;
                persistedRelocation.Error =
                    $"Directory ownership migration recovery is blocked: {exception.Message}";
                persistedRelocation.UpdatedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(CancellationToken.None);
            }

            var resultRelocation = await db.RootFolderRelocations
                .AsNoTracking()
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

    private static void FinalizeRecoveredMetadataOnlyRelocation(
        RootFolderRelocation relocation,
        DateTime now)
    {
        if (relocation.Mode != RootFolderRelocationMode.MetadataOnly)
        {
            return;
        }

        var skippedCount = relocation.SkippedItems.Count;
        relocation.Status = skippedCount == 0
            ? RootFolderRelocationStatus.Completed
            : RootFolderRelocationStatus.NeedsAttention;
        relocation.ActiveRootFolderId = skippedCount == 0
            ? null
            : relocation.RootFolderId;
        relocation.CompletedAt = skippedCount == 0 ? now : null;
        relocation.Error = skippedCount == 0
            ? null
            : BuildSkippedMetadataError(skippedCount);
        relocation.TargetIdentityEnrollmentState = skippedCount == 0
            ? TargetIdentityEnrollmentState.NotRequired
            : relocation.TargetIdentityEnrollmentState;
        relocation.UpdatedAt = now;
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
        var sourceSemantics = new FileSystemPathSemantics(
            plans[0].Journal.SourcePathSyntax,
            plans[0].Journal.SourceCaseSensitivity);
        var targetSemantics = new FileSystemPathSemantics(
            plans[0].Journal.TargetPathSyntax,
            plans[0].Journal.TargetCaseSensitivity);
        var sourcePathAvailable =
            FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                relocation.SourcePath,
                out var canonicalSourcePath,
                out var sourceReason);
        var targetPathAvailable =
            FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                relocation.TargetPath,
                out var canonicalTargetPath,
                out var targetReason);
        if (!sourcePathAvailable || !targetPathAvailable)
        {
            throw new InvalidOperationException(
                $"The relocation path is unavailable for ownership recovery: {sourceReason}{targetReason}");
        }

        var targetResolution = await semanticsResolver.ResolveAsync(
            canonicalTargetPath,
            relocation.TargetCaseSensitivityMode,
            cancellationToken);
        if (targetResolution.State != PathIdentityState.Valid
            || targetResolution.Semantics != targetSemantics)
        {
            throw new InvalidOperationException(
                "The relocation target semantics changed before ownership recovery.");
        }

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
        var audiobookIds = audiobookRows
            .Select(row => row.Audiobook.Id)
            .ToList();
        await db.AudiobookFiles
            .Where(file => audiobookIds.Contains(file.AudiobookId))
            .LoadAsync(cancellationToken);
        var candidates = audiobookRows
            .Select(row => new AudiobookPathCandidate(
                row.Audiobook,
                row.StoredBasePath))
            .ToList();
        var (affected, invalid) = DiscoverAffectedAudiobooks(
            candidates,
            canonicalSourcePath,
            sourceSemantics,
            detectAmbiguousCaseMatches: false);

        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var candidate in affected)
        {
            var destination = MapTargetPath(
                relocation.SourcePath,
                relocation.TargetPath,
                candidate.StoredBasePath,
                sourceSemantics,
                targetSemantics);
            AudiobookPathReferenceRewriter.Rewrite(
                candidate.Audiobook,
                candidate.StoredBasePath,
                destination,
                sourceSemantics,
                targetSemantics,
                relocation.TargetCaseSensitivityMode);
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
                        Reason =
                            "Stored audiobook base path is invalid or case-ambiguous and could not be compared safely with the source root.",
                        CreatedAt =
                            timeProvider.GetUtcNow()
                    });
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        ApplyOwnershipMigrationMetadata(plans, now);
        BeforeOwnershipMigrationMetadataSaveForTest?.Invoke();
        await db.SaveChangesAsync(cancellationToken);
        AssignOwnershipMigrationKeys(plans, now);
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
        root.DirectoryObjectIdentityVersion =
            relocation.TargetDirectoryObjectIdentityVersion;
        root.DirectoryObjectIdentity =
            relocation.TargetDirectoryObjectIdentity;
        root.DirectoryObjectIdentityUnavailableReason =
            relocation.TargetDirectoryObjectIdentityUnavailableReason;
        relocation.CompletedJobs = affected.Count;
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
        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
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
