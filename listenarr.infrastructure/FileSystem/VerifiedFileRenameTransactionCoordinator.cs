using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class VerifiedFileRenameTransactionCoordinator(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IRootFolderRepository rootFolderRepository,
    IRootFolderStorageHealthResolver storageHealthResolver,
    TimeProvider timeProvider,
    ILogger<VerifiedFileRenameTransactionCoordinator> logger)
    : IVerifiedFileRenameTransactionCoordinator
{
    private const string StagingPrefix = ".listenarr-organize-";

    internal Action? AfterJournalPlannedForTest { get; set; }
    internal Action? AfterTargetPublicationForTest { get; set; }
    internal Action? AfterSourceQuarantinedForTest { get; set; }
    internal Action? BeforeRetirementDeleteForTest { get; set; }

    public async Task<VerifiedFileRenamePreparationResult> PrepareAsync(
        string source,
        string destination,
        Guid operationId,
        Guid batchId,
        VerifiedFileRenameBatchManifest batchManifest,
        int audiobookId,
        int audiobookFileId,
        FilePublicationSourceProof sourceProof,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A verified organize operation ID is required.",
                nameof(operationId));
        }
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException(
                "A verified organize batch ID is required.",
                nameof(batchId));
        }
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }
        if (audiobookFileId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookFileId));
        }

        sourceProof.Validate();
        batchManifest.Validate();
        var sourcePath = Path.GetFullPath(source);
        var destinationPath = Path.GetFullPath(destination);
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            return new VerifiedFileRenamePreparationResult(
                false,
                Error: "Verified organize requires distinct source and destination paths.");
        }

        var sourceParentPath = Path.GetDirectoryName(sourcePath);
        var destinationParentPath = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(sourceParentPath)
            || string.IsNullOrWhiteSpace(destinationParentPath))
        {
            return new VerifiedFileRenamePreparationResult(
                false,
                Error: "Verified organize requires source and destination parent directories.");
        }

        var roots = await rootFolderRepository.GetAllAsync();
        var sourceRoot = FindContainingRoot(sourcePath, roots);
        var destinationRoot = FindContainingRoot(destinationPath, roots);
        if (sourceRoot == null || destinationRoot == null)
        {
            return new VerifiedFileRenamePreparationResult(
                false,
                Error: "Verified organize requires configured source and destination roots with persisted path semantics.");
        }

        var sourceHealth = await storageHealthResolver.ResolveAsync(
            sourceRoot,
            cancellationToken);
        var destinationHealth = await storageHealthResolver.ResolveAsync(
            destinationRoot,
            cancellationToken);
        if (!sourceHealth.CanRetireVerifiedSource
            || !destinationHealth.CanPublishAdditively)
        {
            return new VerifiedFileRenamePreparationResult(
                false,
                Error: "Current storage capabilities do not authorize verified organize publication and live source retirement.");
        }

        var sourceName = Path.GetFileName(sourcePath);
        var destinationName = Path.GetFileName(destinationPath);
        var operationName = operationId.ToString("N");
        var stagingName = StagingPrefix + operationName + ".partial";
        var stagingPath = Path.Join(destinationParentPath, stagingName);
        var retirementPath = Path.Join(
            sourceParentPath,
            StagingPrefix + operationName + ".source");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var journal = new VerifiedFileRenameJournal
        {
            OperationId = operationId,
            BatchId = batchId,
            ProtocolVersion = VerifiedFileRenameProtocol.Current,
            AudiobookId = audiobookId,
            AudiobookFileId = audiobookFileId,
            ExpectedBatchMemberCount = batchManifest.ExpectedMemberCount,
            ExpectedBatchManifestSha256 = batchManifest.ManifestSha256,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            StagingPath = stagingPath,
            RetirementPath = retirementPath,
            SourceLength = sourceProof.Length,
            SourceSha256 = sourceProof.Sha256,
            SourceRootFolderId = sourceRoot.Id,
            SourceStorageContractRevision = sourceRoot.StorageContractRevision,
            DestinationRootFolderId = destinationRoot.Id,
            DestinationStorageContractRevision = destinationRoot.StorageContractRevision,
            State = VerifiedFileRenameState.Planned,
            CreatedAt = now,
            UpdatedAt = now
        };

        PinnedDirectoryCreation.PinnedDirectoryAnchor? sourceParent = null;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? destinationParent = null;
        PinnedDirectoryCreation.PinnedFileEntry? sourceEntry = null;
        PinnedDirectoryCreation.PinnedFileEntry? targetEntry = null;
        var journalPersisted = false;
        try
        {
            sourceParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                sourceParentPath);
            destinationParent = OpenOrCreateVerifiedDestinationParent(
                destinationRoot,
                destinationParentPath);
            sourceEntry = sourceParent.OpenExistingFileForStableDelete(sourceName);
            if (!sourceEntry.IsRegularFile()
                || !sourceEntry.VisiblePathMatches()
                || !await sourceEntry.MatchesAsync(
                    sourceProof.Length,
                    sourceProof.Sha256,
                    cancellationToken)
                || (sourceProof.HasDurablePhysicalObjectIdentity
                    && !sourceEntry.MatchesObjectIdentity(
                        sourceProof.PhysicalObjectIdentity)))
            {
                return new VerifiedFileRenamePreparationResult(
                    false,
                    Error: "The organize source changed before verified publication.");
            }

            await PersistNewJournalAsync(journal, cancellationToken);
            journalPersisted = true;
            AfterJournalPlannedForTest?.Invoke();

            targetEntry = destinationParent.CreateNewFile(
                stagingName,
                hiddenFile: true);
            await CopyAndVerifyAsync(
                sourceEntry,
                targetEntry,
                sourceProof,
                cancellationToken);
            var publish = targetEntry.TryMoveToNoReplace(
                destinationParent,
                destinationName);
            if (!publish.Published)
            {
                throw new IOException(
                    $"The verified organize destination could not be published without replacement (native error {publish.NativeErrorCode}).");
            }
            destinationParent.FlushDirectoryEntry();
            if (!targetEntry.VisiblePathMatches()
                || !await targetEntry.MatchesAsync(
                    sourceProof.Length,
                    sourceProof.Sha256,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "The verified organize target changed after publication.");
            }

            AfterTargetPublicationForTest?.Invoke();
            await AdvanceAsync(
                operationId,
                VerifiedFileRenameState.TargetVerified,
                error: null,
                CancellationToken.None);

            var lease = new VerifiedFileRenameLease(
                this,
                journal,
                sourceParent,
                destinationParent,
                sourceEntry,
                targetEntry,
                sourceProof,
                logger);
            sourceParent = null;
            destinationParent = null;
            sourceEntry = null;
            targetEntry = null;
            return new VerifiedFileRenamePreparationResult(true, lease);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException
                or StackOverflowException))
        {
            logger.LogWarning(
                exception,
                "Verified organize operation {OperationId} could not prepare {Source} -> {Destination}",
                operationId,
                sourcePath,
                destinationPath);
            if (journalPersisted)
            {
                await TryRollbackPreparedTargetAsync(
                    operationId,
                    destinationParent,
                    targetEntry,
                    CancellationToken.None);
            }
            return new VerifiedFileRenamePreparationResult(
                false,
                Error: "The verified organize file publication failed safely.");
        }
        finally
        {
            targetEntry?.Dispose();
            sourceEntry?.Dispose();
            destinationParent?.Dispose();
            sourceParent?.Dispose();
        }
    }
}
