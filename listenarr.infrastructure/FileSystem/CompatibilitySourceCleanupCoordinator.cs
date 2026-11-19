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

        if (journals.All(journal =>
                journal.State == CompatibilityFilePublicationState.Completed))
        {
            if (journals.All(journal =>
                    journal.ProtocolVersion == CompatibilityFilePublicationProtocol.Current
                    && journal.RequestedAction == FileAction.Move
                    && journal.CleanupOwner == CompatibilityCleanupOwner.DownloadClient
                    && journal.SourceDisposition
                        == CompatibilitySourceDisposition.DeferredToDownloadClient)
                && HasPersistedBatchManifest(journals)
                && BatchManifestMatches(journals)
                && await CurrentCapabilitiesStillAuthorizeAsync(
                    context,
                    journals,
                    cancellationToken)
                && journals.All(journal => ContentMatches(
                    journal.DestinationPath,
                    journal.TargetLength ?? journal.SourceLength,
                    journal.TargetSha256 ?? journal.SourceSha256)))
            {
                return new CompatibilityBatchCleanupResult(
                    CompatibilityBatchCleanupDisposition.DeferredToDownloadClient,
                    RetainedCount: journals.Count);
            }

            if (journals.All(journal =>
                    journal.SourceDisposition
                        == CompatibilitySourceDisposition.RetiredByListenarr))
            {
                return new CompatibilityBatchCleanupResult(
                    CompatibilityBatchCleanupDisposition.RetiredByListenarr,
                    RemovedCount: journals.Count);
            }

            return new CompatibilityBatchCleanupResult(
                CompatibilityBatchCleanupDisposition.Retained,
                RetainedCount: journals.Count);
        }

        if (!batchSucceeded
            || journals.Any(journal =>
                journal.ProtocolVersion != CompatibilityFilePublicationProtocol.Current
                || journal.State != CompatibilityFilePublicationState.RegistrationCommitted
                || journal.RequestedAction != FileAction.Move
                || journal.CleanupOwner == CompatibilityCleanupOwner.None)
            || !BatchManifestMatches(journals)
            || !await CurrentCapabilitiesStillAuthorizeAsync(
                context,
                journals,
                cancellationToken)
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

        // This contract carries only batch/journal facts. It never receives the
        // original publication source lease, so reopening a matching pathname
        // cannot grant authority to quarantine or delete a surviving source.
        await RetainBatchAsync(context, journals, cancellationToken);
        logger.LogInformation(
            "Compatibility batch {BatchId} completed with sources retained because no original live cleanup proof was supplied",
            batchId);
        return new CompatibilityBatchCleanupResult(
            CompatibilityBatchCleanupDisposition.Retained,
            RetainedCount: journals.Count);
    }

    private async Task<bool> CurrentCapabilitiesStillAuthorizeAsync(
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
                || destination.StorageContractRevision
                    != journal.DestinationStorageContractRevision
                || !(await ResolveHealthAsync(destination)).CanPublishAdditively)
            {
                return false;
            }

            if (journal.SourceRootFolderId is int sourceId
                && (!roots.TryGetValue(sourceId, out var source)
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

}
