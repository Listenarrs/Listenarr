using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    internal bool TryRollbackUncommittedRegistrationPublication(
        RegistrationPublicationCleanupCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var current = TryReadRegistrationPublicationCleanupCandidate(
            candidate.StateDirectoryPath);
        if (current == null
            || string.IsNullOrWhiteSpace(current.SourcePath)
            || string.IsNullOrWhiteSpace(
                current.SourcePhysicalObjectIdentity)
            || current.AudiobookId != candidate.AudiobookId
            || !string.Equals(
                current.StateName,
                candidate.StateName,
                StringComparison.Ordinal)
            || !string.Equals(
                CanonicalizeDurablePathEvidence(current.DestinationPath),
                CanonicalizeDurablePathEvidence(candidate.DestinationPath),
                StringComparison.Ordinal)
            || !string.Equals(
                CanonicalizeDurablePathEvidence(current.SourcePath),
                CanonicalizeDurablePathEvidence(candidate.SourcePath ?? string.Empty),
                StringComparison.Ordinal)
            || !string.Equals(
                current.PhysicalObjectIdentity,
                candidate.PhysicalObjectIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                current.SourcePhysicalObjectIdentity,
                candidate.SourcePhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var destinationParentPath = Path.GetDirectoryName(
                current.DestinationPath);
            var sourceParentPath = Path.GetDirectoryName(current.SourcePath);
            if (string.IsNullOrWhiteSpace(destinationParentPath)
                || string.IsNullOrWhiteSpace(sourceParentPath))
            {
                return false;
            }

            using var destinationParent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    destinationParentPath,
                    createMissing: false);
            using var sourceParent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    sourceParentPath,
                    createMissing: false);
            using var source = sourceParent.TryOpenExistingFile(
                Path.GetFileName(current.SourcePath),
                requireDeleteAccess: false);
            using var statePublication = destinationParent
                .TryOpenExistingChildForPublication(current.StateName);
            if (source == null
                || statePublication == null
                || !source.VisiblePathMatches()
                || !string.Equals(
                    source.GetObjectIdentity(),
                    current.SourcePhysicalObjectIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }

            using var state = statePublication.OpenCreatedDirectoryAnchor();
            if (!destinationParent.VisiblePathMatches()
                || !sourceParent.VisiblePathMatches()
                || !state.VisiblePathMatches()
                || !AnchoredStateContainsOnly(
                    state,
                    "publication.claim",
                    RegistrationCleanupIntentName))
            {
                return false;
            }

            var published = destinationParent.TryOpenExistingFile(
                Path.GetFileName(current.DestinationPath),
                requireDeleteAccess: true);
            var claim = state.TryOpenExistingFile(
                "publication.claim",
                requireDeleteAccess: true);
            var intent = state.TryOpenExistingFile(
                RegistrationCleanupIntentName,
                requireDeleteAccess: true);
            try
            {
                if (intent == null
                    || !intent.VisiblePathMatches()
                    || (published != null
                        && (!published.VisiblePathMatches()
                            || !source.IdentifiesSameEntry(published)))
                    || (claim != null
                        && (!claim.VisiblePathMatches()
                            || !source.IdentifiesSameEntry(claim))))
                {
                    return false;
                }

                if (published != null)
                {
                    published.Delete(immediateWindows: true);
                    published.Dispose();
                    published = null;
                    FlushFileMoveDirectory(
                        destinationParent,
                        "uncommitted registration destination rollback");
                    AfterUncommittedRegistrationDestinationRetiredForTest
                        ?.Invoke();
                }

                if (claim != null)
                {
                    claim.Delete(immediateWindows: true);
                    claim.Dispose();
                    claim = null;
                    FlushFileMoveDirectory(
                        state,
                        "uncommitted registration claim rollback");
                }

                intent.Delete(immediateWindows: true);
                intent.Dispose();
                intent = null;
                FlushFileMoveDirectory(
                    state,
                    "uncommitted registration intent rollback");
            }
            finally
            {
                published?.Dispose();
                claim?.Dispose();
                intent?.Dispose();
            }

            state.Dispose();
            statePublication.RetirePinnedEmptyDirectoryFromNamespace(
                current.StateName);
            FlushFileMoveDirectory(
                destinationParent,
                "uncommitted registration state rollback");
            return !File.Exists(current.DestinationPath)
                && File.Exists(current.SourcePath);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException
                or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Could not roll back uncommitted registration publication at {Destination}",
                LogRedaction.SanitizeFilePath(candidate.DestinationPath));
            return false;
        }
    }
}
