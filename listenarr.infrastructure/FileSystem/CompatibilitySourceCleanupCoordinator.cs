using System.Text.Json;
using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class CompatibilitySourceCleanupCoordinator(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IRootFolderStorageHealthResolver storageHealthResolver,
    TimeProvider timeProvider,
    ILogger<CompatibilitySourceCleanupCoordinator> logger)
    : ICompatibilitySourceCleanupCoordinator
{
    private const string QuarantineDirectoryPrefix = ".listenarr-quarantine-";
    private const string OwnershipMarkerName = ".listenarr-owner-v2";

    internal Action? AfterSourceMovedToQuarantineForTest { get; set; }
    internal Action? AfterBatchQuarantinedForTest { get; set; }
    internal Action<CompatibilityFilePublicationJournal>? BeforeSourceDeleteForTest { get; set; }

    public async Task<CompatibilityBatchCleanupResult> CompleteBatchAsync(
        Guid batchId,
        bool batchSucceeded,
        CancellationToken cancellationToken = default)
    {
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("A compatibility batch ID is required.", nameof(batchId));
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var journals = await context.CompatibilityFilePublicationJournals
            .Where(journal => journal.BatchId == batchId)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .ToListAsync(cancellationToken);
        if (journals.Count == 0)
        {
            return new CompatibilityBatchCleanupResult(
                CompatibilityBatchCleanupDisposition.NotApplicable);
        }

        if (!batchSucceeded
            || journals.Any(journal =>
                journal.ProtocolVersion != CompatibilityFilePublicationProtocol.Current
                || journal.State != CompatibilityFilePublicationState.RegistrationCommitted
                || journal.RequestedAction != FileAction.Move
                || journal.CleanupOwner == CompatibilityCleanupOwner.None)
            || !await PoliciesStillAuthorizeAsync(context, journals, cancellationToken)
            || journals.Any(journal => !ContentMatches(
                journal.DestinationPath,
                journal.SourceLength,
                journal.SourceSha256)))
        {
            await RetainBatchAsync(context, journals, cancellationToken);
            return new CompatibilityBatchCleanupResult(
                CompatibilityBatchCleanupDisposition.Retained,
                RetainedCount: journals.Count);
        }

        var owner = journals[0].CleanupOwner;
        if (journals.Any(journal => journal.CleanupOwner != owner))
        {
            await RetainBatchAsync(context, journals, cancellationToken);
            return new CompatibilityBatchCleanupResult(
                CompatibilityBatchCleanupDisposition.Retained,
                RetainedCount: journals.Count);
        }

        if (owner == CompatibilityCleanupOwner.DownloadClient)
        {
            foreach (var journal in journals)
            {
                journal.SourceDisposition =
                    CompatibilitySourceDisposition.DeferredToDownloadClient;
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            }
            await context.SaveChangesAsync(cancellationToken);
            return new CompatibilityBatchCleanupResult(
                CompatibilityBatchCleanupDisposition.DeferredToDownloadClient,
                RetainedCount: journals.Count);
        }

        foreach (var journal in journals)
        {
            journal.State = CompatibilityFilePublicationState.SourceDeleteAuthorized;
            journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        }
        await context.SaveChangesAsync(cancellationToken);

        var quarantined = new List<(CompatibilityFilePublicationJournal Journal, string OriginalName)>();
        try
        {
            foreach (var journal in journals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceParentPath = Path.GetDirectoryName(journal.SourcePath)
                    ?? throw new InvalidOperationException("The cleanup source has no parent directory.");
                var sourceName = Path.GetFileName(journal.SourcePath);
                var quarantineDirectory = Path.Join(
                    sourceParentPath,
                    QuarantineDirectoryPrefix + batchId.ToString("N"));
                PrepareQuarantineDirectory(quarantineDirectory, batchId);
                var quarantineName = journal.OperationId.ToString("N") + ".source";
                var quarantinePath = Path.Join(quarantineDirectory, quarantineName);

                journal.QuarantinePath = quarantinePath;
                journal.State = CompatibilityFilePublicationState.SourceQuarantinePlanned;
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await context.SaveChangesAsync(CancellationToken.None);

                using var sourceParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                    sourceParentPath);
                using var quarantineParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                    quarantineDirectory);
                using var source = sourceParent.OpenExistingFileForStableDelete(sourceName);
                if (!source.IsOnSameVolume(quarantineParent)
                    || !await source.MatchesAsync(
                        journal.SourceLength,
                        journal.SourceSha256,
                        CancellationToken.None))
                {
                    throw new InvalidOperationException(
                        "The compatibility source changed before quarantine.");
                }

                source.MoveTo(quarantineParent, quarantineName);
                // Once the rename succeeds, this source must participate in rollback even
                // if verification, flushing, or journal persistence fails afterward.
                quarantined.Add((journal, sourceName));
                AfterSourceMovedToQuarantineForTest?.Invoke();
                sourceParent.FlushDirectoryEntry();
                quarantineParent.FlushDirectoryEntry();
                if (!await source.MatchesAsync(
                        journal.SourceLength,
                        journal.SourceSha256,
                        CancellationToken.None))
                {
                    throw new InvalidOperationException(
                        "The quarantined source failed content verification.");
                }

                journal.State = CompatibilityFilePublicationState.SourceQuarantined;
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await context.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            logger.LogWarning(
                exception,
                "Compatibility batch {BatchId} could not quarantine every source; restoring without overwrite",
                batchId);
            await RestoreQuarantinedAsync(
                context,
                batchId,
                journals,
                exception.Message);
            return new CompatibilityBatchCleanupResult(
                CompatibilityBatchCleanupDisposition.Retained,
                RetainedCount: journals.Count,
                FailedPaths: journals.Select(journal => journal.SourcePath).ToList());
        }

        AfterBatchQuarantinedForTest?.Invoke();
        if (journals.Any(journal => !ContentMatches(
                journal.DestinationPath,
                journal.TargetLength ?? journal.SourceLength,
                journal.TargetSha256 ?? journal.SourceSha256)))
        {
            await RestoreQuarantinedAsync(
                context,
                batchId,
                journals,
                "A verified destination changed before source deletion.");
            return new CompatibilityBatchCleanupResult(
                CompatibilityBatchCleanupDisposition.Retained,
                RetainedCount: journals.Count);
        }

        var removed = 0;
        var failed = new List<string>();
        foreach (var (journal, _) in quarantined)
        {
            BeforeSourceDeleteForTest?.Invoke(journal);
            if (!ContentMatches(
                    journal.DestinationPath,
                    journal.TargetLength ?? journal.SourceLength,
                    journal.TargetSha256 ?? journal.SourceSha256))
            {
                await RestoreQuarantinedAsync(
                    context,
                    batchId,
                    journals,
                    "A verified destination changed immediately before source deletion.");
                return new CompatibilityBatchCleanupResult(
                    removed == 0
                        ? CompatibilityBatchCleanupDisposition.Retained
                        : CompatibilityBatchCleanupDisposition.PartialNeedsAttention,
                    RemovedCount: removed,
                    RetainedCount: journals.Count - removed,
                    FailedPaths: [journal.SourcePath]);
            }

            try
            {
                var quarantinePath = journal.QuarantinePath!;
                var quarantineParentPath = Path.GetDirectoryName(quarantinePath)!;
                using var quarantineParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                    quarantineParentPath);
                using var quarantinedFile = quarantineParent.OpenExistingFileForStableDelete(
                    Path.GetFileName(quarantinePath));
                if (!await quarantinedFile.MatchesAsync(
                        journal.SourceLength,
                        journal.SourceSha256,
                        CancellationToken.None))
                {
                    throw new InvalidOperationException(
                        "The quarantined source changed before deletion.");
                }
                quarantinedFile.Delete(immediateWindows: true);
                quarantineParent.FlushDirectoryEntry();
                journal.State = CompatibilityFilePublicationState.SourceDeleted;
                journal.SourceDisposition = CompatibilitySourceDisposition.RetiredByListenarr;
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await context.SaveChangesAsync(CancellationToken.None);
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await context.SaveChangesAsync(CancellationToken.None);
                removed++;
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException))
            {
                failed.Add(journal.SourcePath);
                journal.SourceDisposition = CompatibilitySourceDisposition.PartialNeedsAttention;
                journal.State = CompatibilityFilePublicationState.NeedsAttention;
                journal.Error = "Quarantined source cleanup failed: " + exception.Message;
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await context.SaveChangesAsync(CancellationToken.None);
            }
        }

        if (failed.Count == 0)
        {
            foreach (var quarantineDirectory in quarantined
                .Select(item => Path.GetDirectoryName(item.Journal.QuarantinePath!))
                .Where(path => path != null)
                .Distinct(StringComparer.Ordinal))
            {
                TryRemoveEmptyOwnedQuarantine(quarantineDirectory!, batchId);
            }

            return new CompatibilityBatchCleanupResult(
                CompatibilityBatchCleanupDisposition.RetiredByListenarr,
                RemovedCount: removed);
        }

        return new CompatibilityBatchCleanupResult(
            CompatibilityBatchCleanupDisposition.PartialNeedsAttention,
            RemovedCount: removed,
            RetainedCount: failed.Count,
            FailedPaths: failed);
    }

    private async Task<bool> PoliciesStillAuthorizeAsync(
        ListenArrDbContext context,
        IReadOnlyCollection<CompatibilityFilePublicationJournal> journals,
        CancellationToken cancellationToken)
    {
        var rootIds = journals
            .SelectMany(journal => new int?[]
            {
                journal.SourceRootFolderId,
                journal.DestinationRootFolderId
            })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var roots = await context.RootFolders
            .AsNoTracking()
            .Where(root => rootIds.Contains(root.Id))
            .ToDictionaryAsync(root => root.Id, cancellationToken);

        var healthByRootId = new Dictionary<int, RootFolderStorageObservation>();
        foreach (var journal in journals)
        {
            if (journal.DestinationRootFolderId is not int destinationId
                || !roots.TryGetValue(destinationId, out var destination)
                || destination.WeakStorageSourceCleanupPolicy
                    != WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy
                || destination.WeakStoragePolicyRevision
                    != journal.DestinationPolicyRevision
                || destination.StorageContractRevision
                    != journal.DestinationStorageContractRevision
                || !(await ResolveHealthAsync(destination)).CanPublishAdditively)
            {
                return false;
            }
            if (journal.SourceRootFolderId is int sourceId
                && (!roots.TryGetValue(sourceId, out var source)
                    || source.WeakStorageSourceCleanupPolicy
                        != WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy
                    || source.WeakStoragePolicyRevision != journal.SourcePolicyRevision
                    || source.StorageContractRevision
                        != journal.SourceStorageContractRevision
                    || !(await ResolveHealthAsync(source)).CanRetireVerifiedSource))
            {
                return false;
            }
        }
        return true;

        async Task<RootFolderStorageObservation> ResolveHealthAsync(RootFolder root)
        {
            if (healthByRootId.TryGetValue(root.Id, out var cached))
            {
                return cached;
            }

            var resolved = await storageHealthResolver.ResolveAsync(
                root,
                cancellationToken);
            healthByRootId[root.Id] = resolved;
            return resolved;
        }
    }

    private async Task RestoreQuarantinedAsync(
        ListenArrDbContext context,
        Guid batchId,
        IReadOnlyList<CompatibilityFilePublicationJournal> journals,
        string failure)
    {
        foreach (var journal in journals
            .Where(candidate => candidate.State is
                CompatibilityFilePublicationState.SourceQuarantinePlanned or
                CompatibilityFilePublicationState.SourceQuarantined)
            .Reverse())
        {
            var quarantineMatches = !string.IsNullOrWhiteSpace(journal.QuarantinePath)
                && ContentMatches(
                    journal.QuarantinePath,
                    journal.SourceLength,
                    journal.SourceSha256);
            var sourceMatches = ContentMatches(
                journal.SourcePath,
                journal.SourceLength,
                journal.SourceSha256);

            if (sourceMatches && !quarantineMatches)
            {
                journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.Error = "Source cleanup was aborted before quarantine: " + failure;
                journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                await context.SaveChangesAsync(CancellationToken.None);
                continue;
            }

            if (!sourceMatches && quarantineMatches)
            {
                try
                {
                    var quarantinePath = journal.QuarantinePath!;
                    using var quarantineParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                        Path.GetDirectoryName(quarantinePath)!);
                    using var originalParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                        Path.GetDirectoryName(journal.SourcePath)!);
                    using var file = quarantineParent.OpenExistingFileForStableDelete(
                        Path.GetFileName(quarantinePath));
                    file.MoveTo(originalParent, Path.GetFileName(journal.SourcePath));
                    quarantineParent.FlushDirectoryEntry();
                    originalParent.FlushDirectoryEntry();
                    if (!ContentMatches(
                            journal.SourcePath,
                            journal.SourceLength,
                            journal.SourceSha256))
                    {
                        throw new InvalidOperationException(
                            "The restored compatibility source could not be reverified.");
                    }

                    journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
                    journal.State = CompatibilityFilePublicationState.Completed;
                    journal.Error = "Source cleanup was aborted and restored: " + failure;
                    journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                }
                catch (Exception restoreException) when (restoreException is not (
                    OutOfMemoryException or StackOverflowException))
                {
                    journal.SourceDisposition = CompatibilitySourceDisposition.PartialNeedsAttention;
                    journal.State = CompatibilityFilePublicationState.NeedsAttention;
                    journal.Error = "Source cleanup failed and automatic restore was not safe: "
                        + restoreException.Message;
                    journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
                }
                await context.SaveChangesAsync(CancellationToken.None);
                continue;
            }

            journal.SourceDisposition = CompatibilitySourceDisposition.PartialNeedsAttention;
            journal.State = CompatibilityFilePublicationState.NeedsAttention;
            journal.Error = sourceMatches && quarantineMatches
                ? "Source cleanup failed and both the source and quarantine are present: " + failure
                : "Source cleanup failed and neither the source nor quarantine can be verified: " + failure;
            journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(CancellationToken.None);
        }

        foreach (var journal in journals.Where(candidate => candidate.State is
            CompatibilityFilePublicationState.RegistrationCommitted or
            CompatibilityFilePublicationState.SourceDeleteAuthorized))
        {
            journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
            journal.State = CompatibilityFilePublicationState.Completed;
            journal.Error = "Source cleanup was aborted before quarantine: " + failure;
            journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        }
        await context.SaveChangesAsync(CancellationToken.None);

        foreach (var quarantineDirectory in journals
            .Select(journal => journal.QuarantinePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetDirectoryName(path!))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal))
        {
            TryRemoveEmptyOwnedQuarantine(quarantineDirectory!, batchId);
        }
    }

    private void TryRemoveEmptyOwnedQuarantine(string path, Guid batchId)
    {
        try
        {
            var markerPath = Path.Join(path, OwnershipMarkerName);
            var expectedMarker = JsonSerializer.Serialize(new
            {
                ProtocolVersion = CompatibilityFilePublicationProtocol.Current,
                BatchId = batchId
            });
            if (!File.Exists(markerPath)
                || !string.Equals(
                    File.ReadAllText(markerPath),
                    expectedMarker,
                    StringComparison.Ordinal)
                || Directory.EnumerateFileSystemEntries(path)
                    .Any(entry => !string.Equals(entry, markerPath, StringComparison.Ordinal)))
            {
                return;
            }

            File.Delete(markerPath);
            Directory.Delete(path, recursive: false);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            logger.LogDebug(
                exception,
                "Could not remove empty compatibility quarantine {QuarantinePath}",
                path);
        }
    }

    private async Task RetainBatchAsync(
        ListenArrDbContext context,
        IReadOnlyCollection<CompatibilityFilePublicationJournal> journals,
        CancellationToken cancellationToken)
    {
        foreach (var journal in journals.Where(journal =>
            journal.State == CompatibilityFilePublicationState.RegistrationCommitted))
        {
            journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
            journal.State = CompatibilityFilePublicationState.Completed;
            journal.Error = null;
            journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        }
        await context.SaveChangesAsync(cancellationToken);
    }

}
