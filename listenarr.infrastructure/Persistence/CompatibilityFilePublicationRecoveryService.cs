using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

internal interface ICompatibilityFilePublicationRecoveryService
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}

internal sealed class CompatibilityFilePublicationRecoveryService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<CompatibilityFilePublicationRecoveryService> logger)
    : ICompatibilityFilePublicationRecoveryService
{
    internal Action? BeforeQuarantineRestoreForTest { get; set; }

    public async Task ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        await using var readContext = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var operationIds = await readContext.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.State != CompatibilityFilePublicationState.Completed
                && journal.State != CompatibilityFilePublicationState.NeedsAttention)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => journal.OperationId)
            .ToListAsync(cancellationToken);

        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileOperationAsync(operationId, cancellationToken);
        }

        await RecoverManifestedBatchesAsync(cancellationToken);
    }

    private async Task ReconcileOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var journal = await context.CompatibilityFilePublicationJournals
            .SingleOrDefaultAsync(
                candidate => candidate.OperationId == operationId,
                cancellationToken);
        if (journal == null
            || journal.State is CompatibilityFilePublicationState.Completed
                or CompatibilityFilePublicationState.NeedsAttention)
        {
            return;
        }

        if (journal.ProtocolVersion is not (
                CompatibilityFilePublicationProtocol.RetainOnly or
                CompatibilityFilePublicationProtocol.Current))
        {
            MarkNeedsAttention(
                journal,
                "The compatibility publication protocol is unsupported.");
        }
        else if (journal.State == CompatibilityFilePublicationState.Planned)
        {
            if (File.Exists(journal.DestinationPath))
            {
                MarkNeedsAttention(
                    journal,
                    "A destination exists for an unverified compatibility publication. It was preserved without overwrite or deletion.");
            }
            else if (!ContentMatches(
                journal.SourcePath,
                journal.SourceLength,
                journal.SourceSha256))
            {
                MarkNeedsAttention(
                    journal,
                    "The planned compatibility source is missing or changed.");
            }
            else
            {
                return;
            }
        }
        else if (journal.ProtocolVersion == CompatibilityFilePublicationProtocol.Current
            && journal.State is
                CompatibilityFilePublicationState.SourceDeleteAuthorized or
                CompatibilityFilePublicationState.SourceQuarantinePlanned or
                CompatibilityFilePublicationState.SourceQuarantined or
                CompatibilityFilePublicationState.SourceDeleted)
        {
            await ReconcileInterruptedCleanupAsync(context, journal, cancellationToken);
        }
        else if (!await TargetContentMatchesAsync(context, journal, cancellationToken))
        {
            MarkNeedsAttention(
                journal,
                "The verified compatibility destination is missing or changed.");
        }
        else if (journal.State
            == CompatibilityFilePublicationState.RegistrationCommitted)
        {
            var hasOwner = journal.IsCompanionFile
                || (journal.AudiobookId is int audiobookId
                && await context.AudiobookFiles
                    .AsNoTracking()
                    .AnyAsync(
                        file => file.AudiobookId == audiobookId
                            && (file.Path == journal.DestinationPath
                                || file.CanonicalPath == journal.DestinationPath),
                        cancellationToken));
            if (!hasOwner)
            {
                MarkNeedsAttention(
                    journal,
                    "The committed compatibility destination no longer has its expected audiobook owner.");
            }
            else if (journal.ProtocolVersion
                    == CompatibilityFilePublicationProtocol.Current
                && journal.CleanupOwner != CompatibilityCleanupOwner.None)
            {
                if (journal.BatchId.HasValue
                    && journal.ExpectedBatchMemberCount.HasValue
                    && !string.IsNullOrWhiteSpace(
                        journal.ExpectedBatchSourceManifestSha256))
                {
                    // A sealed manifest can be revalidated after every operation-level
                    // recovery pass completes. Leave this journal committed so the
                    // batch coordinator can decide the whole batch atomically.
                    return;
                }

                // Released verified-cleanup journals predate persisted manifests.
                // Without a durable expected-member set, startup cannot prove that
                // another source should have produced a journal, so fail closed.
                journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.Error = "Interrupted compatibility batch recovered retain-only.";
            }
            else
            {
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.Error = null;
            }
        }
        else
        {
            // TargetVerified is intentionally resumable only by the original import,
            // which still owns the metadata and destination-planning context.
            return;
        }

        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task RecoverManifestedBatchesAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var batchIds = await context.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.BatchId.HasValue
                && journal.State == CompatibilityFilePublicationState.RegistrationCommitted
                && journal.CleanupOwner != CompatibilityCleanupOwner.None
                && journal.ExpectedBatchMemberCount.HasValue
                && journal.ExpectedBatchSourceManifestSha256 != null)
            .Select(journal => journal.BatchId!.Value)
            .Distinct()
            .OrderBy(batchId => batchId)
            .ToListAsync(cancellationToken);

        foreach (var batchId in batchIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journals = await context.CompatibilityFilePublicationJournals
                .AsNoTracking()
                .Where(journal => journal.BatchId == batchId)
                .ToListAsync(cancellationToken);
            var incompleteSealedBatch = journals.Count > 0
                && journals.All(journal =>
                    journal.ExpectedBatchMemberCount.HasValue
                    && journal.ExpectedBatchMemberCount.Value > 0
                    && !string.IsNullOrWhiteSpace(
                        journal.ExpectedBatchSourceManifestSha256))
                && journals.Select(journal => journal.ExpectedBatchMemberCount!.Value)
                    .Distinct()
                    .Count() == 1
                && journals.Select(journal => journal.ExpectedBatchSourceManifestSha256)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == 1
                && journals[0].ExpectedBatchMemberCount!.Value > journals.Count;
            if (incompleteSealedBatch)
            {
                logger.LogInformation(
                    "Manifested compatibility batch {BatchId} is incomplete at startup ({ObservedCount}/{ExpectedCount}); leaving committed members pending for retry",
                    batchId,
                    journals.Count,
                    journals[0].ExpectedBatchMemberCount!.Value);
                continue;
            }

            if (journals.Any(journal =>
                    journal.State
                        != CompatibilityFilePublicationState.RegistrationCommitted))
            {
                logger.LogWarning(
                    "Manifested compatibility batch {BatchId} has mixed recovery state and was left scoped for operation-level reconciliation",
                    batchId);
                continue;
            }

            var tracked = await context.CompatibilityFilePublicationJournals
                .Where(journal => journal.BatchId == batchId
                    && journal.State
                        == CompatibilityFilePublicationState.RegistrationCommitted)
                .ToListAsync(cancellationToken);
            foreach (var journal in tracked)
            {
                journal.SourceDisposition =
                    CompatibilitySourceDisposition.Retained;
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.Error =
                    "Verified publication recovered after restart; source cleanup authority was lost, so the source was retained.";
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            }
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Recovered manifested compatibility batch {BatchId} retain-only after restart",
                batchId);
        }
    }

    private async Task ReconcileInterruptedCleanupAsync(
        ListenArrDbContext context,
        CompatibilityFilePublicationJournal journal,
        CancellationToken cancellationToken)
    {
        if (!await TargetContentMatchesAsync(context, journal, cancellationToken))
        {
            MarkNeedsAttention(
                journal,
                "The verified destination changed during interrupted source cleanup.");
            return;
        }

        if (journal.State == CompatibilityFilePublicationState.SourceDeleted)
        {
            if (File.Exists(journal.SourcePath)
                || (!string.IsNullOrWhiteSpace(journal.QuarantinePath)
                    && File.Exists(journal.QuarantinePath)))
            {
                journal.SourceDisposition =
                    CompatibilitySourceDisposition.PartialNeedsAttention;
                MarkNeedsAttention(
                    journal,
                    "A source reappeared after source deletion was recorded.");
                return;
            }

            journal.SourceDisposition = CompatibilitySourceDisposition.RetiredByListenarr;
            journal.State = CompatibilityFilePublicationState.Completed;
            journal.Error = null;
            return;
        }

        var sourceMatches = ContentMatches(
            journal.SourcePath,
            journal.SourceLength,
            journal.SourceSha256);
        var quarantineMatches = !string.IsNullOrWhiteSpace(journal.QuarantinePath)
            && ContentMatches(
                journal.QuarantinePath,
                journal.SourceLength,
                journal.SourceSha256);
        if (sourceMatches && !quarantineMatches)
        {
            journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
            journal.State = CompatibilityFilePublicationState.Completed;
            journal.Error = "Interrupted source cleanup recovered retain-only.";
            return;
        }
        if (!sourceMatches && quarantineMatches)
        {
            BeforeQuarantineRestoreForTest?.Invoke();
            try
            {
                var quarantinePath = journal.QuarantinePath!;
                using var quarantineParent =
                    PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                        Path.GetDirectoryName(quarantinePath)!,
                        createMissing: false);
                using var sourceParent =
                    PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                        Path.GetDirectoryName(journal.SourcePath)!,
                        createMissing: false);
                using var quarantined = quarantineParent.OpenExistingFileForStableDelete(
                    Path.GetFileName(quarantinePath));
                if (!quarantined.MatchesAsync(
                        journal.SourceLength, journal.SourceSha256, CancellationToken.None)
                        .GetAwaiter().GetResult()
                    || !quarantined.VisiblePathMatches()
                    || !quarantineParent.VisiblePathMatches()
                    || !sourceParent.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The quarantined source changed before restoration.");
                }
                quarantined.MoveTo(sourceParent, Path.GetFileName(journal.SourcePath));
                if (!quarantined.MatchesAsync(
                        journal.SourceLength, journal.SourceSha256, CancellationToken.None)
                        .GetAwaiter().GetResult()
                    || !quarantined.VisiblePathMatches()
                    || !sourceParent.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The restored source changed before recovery completion.");
                }
                quarantineParent.FlushDirectoryEntry();
                sourceParent.FlushDirectoryEntry();
                journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.Error = "Interrupted source cleanup restored from quarantine.";
                return;
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException))
            {
                journal.SourceDisposition =
                    CompatibilitySourceDisposition.PartialNeedsAttention;
                MarkNeedsAttention(
                    journal,
                    "The quarantined source could not be restored without overwrite: "
                    + exception.Message);
                return;
            }
        }

        journal.SourceDisposition = CompatibilitySourceDisposition.PartialNeedsAttention;
        MarkNeedsAttention(
            journal,
            sourceMatches && quarantineMatches
                ? "Both source and quarantine exist after interrupted cleanup."
                : "Both source and quarantine are missing or changed after interrupted cleanup.");
    }

    private void MarkNeedsAttention(
        CompatibilityFilePublicationJournal journal,
        string reason)
    {
        journal.State = CompatibilityFilePublicationState.NeedsAttention;
        journal.Error = reason;
        logger.LogWarning(
            "Compatibility file publication {OperationId} requires attention: {Reason}",
            journal.OperationId,
            reason);
    }

    private static async Task<bool> TargetContentMatchesAsync(
        ListenArrDbContext context,
        CompatibilityFilePublicationJournal journal,
        CancellationToken cancellationToken)
    {
        RootFolder? root = null;
        if (journal.DestinationRootFolderId is int rootId)
        {
            root = await context.RootFolders.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == rootId, cancellationToken);
            if (root == null
                || (journal.DestinationStorageContractRevision.HasValue
                    && root.StorageContractRevision != journal.DestinationStorageContractRevision.Value))
            {
                return false;
            }
            var persisted = RootFolderPathSemantics.ResolvePersisted(root);
            if (persisted == null || persisted.Value.DetectAmbiguousCaseMatches) return false;
            var current = await new FileSystemSemanticsResolver().ResolveAsync(
                root.Path, root.CaseSensitivityMode, cancellationToken);
            if (current.State != PathIdentityState.Valid
                || current.Semantics != persisted.Value.Semantics) return false;
        }

        return ContentMatches(journal.DestinationPath,
            journal.TargetLength ?? journal.SourceLength,
            journal.TargetSha256 ?? journal.SourceSha256,
            root);
    }

    private static bool ContentMatches(
        string path,
        long length,
        string sha256,
        RootFolder? configuredRoot = null)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parentPath = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            // Only the journal's current configured target boundary may follow
            // links. Descendants, unmanaged sources, and quarantine stay no-follow.
            using var parent = PinnedDirectoryCreation.OpenPinnedConfiguredHierarchy(
                configuredRoot,
                parentPath,
                createMissing: false);
            var outcome = parent.TryOpenExistingFileWithOutcome(
                fileName,
                requireDeleteAccess: false,
                out var openedFile);
            using var file = openedFile;
            return outcome == PinnedFileOpenOutcome.Opened
                && file != null
                && file.IsRegularFile()
                && file.MatchesAsync(length, sha256, CancellationToken.None)
                    .GetAwaiter().GetResult()
                && parent.VisiblePathMatches()
                && file.VisiblePathMatches();
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}
