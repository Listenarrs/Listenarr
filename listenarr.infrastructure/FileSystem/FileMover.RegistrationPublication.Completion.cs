using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<IAudiobookFileRegistrationLease?>
        TryOpenRegisteredHardlinkPublicationAsync(
            string source,
            string destination,
            Guid operationId,
            string expectedPhysicalObjectIdentity)
    {
        using var gate = await TryAcquireFileMoveGateAsync(
            source,
            destination,
            allowExistingAliasForRecovery: true);
        if (gate == null)
        {
            return null;
        }

        using var sourceEntry = gate.SourceParent.TryOpenExistingFile(
            gate.SourceName,
            requireDeleteAccess: false);
        using var destinationEntry = gate.DestinationParent.TryOpenExistingFile(
            gate.DestinationName,
            requireDeleteAccess: false);
        if (sourceEntry == null
            || destinationEntry == null
            || !sourceEntry.VisiblePathMatches()
            || !destinationEntry.VisiblePathMatches()
            || !sourceEntry.IdentifiesSameEntry(destinationEntry)
            || !string.Equals(
                destinationEntry.GetObjectIdentity(),
                expectedPhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            return null;
        }

        var logicalIdentity = GetRegistrationPublicationLogicalIdentity(
            gate,
            operationId);
        var candidateNames = FindRegistrationPublicationStateCandidates(
            gate.DestinationParent,
            logicalIdentity);
        if (candidateNames.Count > 1)
        {
            return null;
        }

        if (candidateNames.Count == 1)
        {
            var stateName = candidateNames[0];
            using var statePublication =
                gate.DestinationParent.TryOpenExistingChildForPublication(stateName);
            if (statePublication == null)
            {
                return null;
            }

            using var state = statePublication.OpenCreatedDirectoryAnchor();
            if (!state.VisiblePathMatches()
                || !AnchoredStateContainsOnly(
                    state,
                    "publication.claim",
                    RegistrationCleanupIntentName))
            {
                return null;
            }

            using var claim = state.TryOpenExistingFile(
                "publication.claim",
                requireDeleteAccess: true);
            using var intent = state.TryOpenExistingFile(
                RegistrationCleanupIntentName,
                requireDeleteAccess: true);
            if (intent != null
                && !RegistrationCleanupIntentMatchesPublication(
                    intent,
                    gate.DestinationName,
                    expectedPhysicalObjectIdentity))
            {
                return null;
            }
            if (claim != null)
            {
                if (!claim.VisiblePathMatches()
                    || !claim.IdentifiesSameEntry(destinationEntry))
                {
                    return null;
                }

                claim.Delete(immediateWindows: true);
                claim.Dispose();
                FlushFileMoveDirectory(
                    state,
                    "registered publication claim retirement");
            }

            if (intent != null
                && !DeletePinnedRegistrationCleanupIntent(state, intent))
            {
                return null;
            }

            state.Dispose();
            statePublication.RetirePinnedEmptyDirectoryFromNamespace(
                stateName);
            FlushFileMoveDirectory(
                gate.DestinationParent,
                "registered publication state retirement");
        }

        return PinnedAudiobookFileRegistrationLease.Create(
            destinationEntry.OpenStableRegistrationCopy(),
            destination,
            expectedPhysicalObjectIdentity,
            sourceEntry.GetObjectIdentity());
    }

    private bool CompleteHardlinkRegistrationPublication(
        string destination,
        string stateName,
        string expectedPhysicalObjectIdentity)
    {
        try
        {
            var parentPath = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            using var statePublication =
                parent.TryOpenExistingChildForPublication(stateName);
            if (statePublication == null)
            {
                using var completedPublication = parent.TryOpenExistingFile(
                    Path.GetFileName(destination),
                    requireDeleteAccess: false);
                return parent.VisiblePathMatches()
                    && completedPublication != null
                    && completedPublication.VisiblePathMatches()
                    && string.Equals(
                        completedPublication.GetObjectIdentity(),
                        expectedPhysicalObjectIdentity,
                        StringComparison.Ordinal);
            }

            using var state = statePublication.OpenCreatedDirectoryAnchor();
            if (!parent.VisiblePathMatches()
                || !state.VisiblePathMatches()
                || !AnchoredStateContainsOnly(
                    state,
                    "publication.claim",
                    RegistrationCleanupIntentName))
            {
                return false;
            }

            using var claim = state.TryOpenExistingFile(
                "publication.claim",
                requireDeleteAccess: true);
            using var intent = state.TryOpenExistingFile(
                RegistrationCleanupIntentName,
                requireDeleteAccess: true);
            using var published = parent.TryOpenExistingFile(
                Path.GetFileName(destination),
                requireDeleteAccess: false);
            if (published == null
                || !published.VisiblePathMatches()
                || !string.Equals(
                    published.GetObjectIdentity(),
                    expectedPhysicalObjectIdentity,
                    StringComparison.Ordinal)
                || (claim == null && intent == null)
                || (claim != null
                    && (!claim.VisiblePathMatches()
                        || !claim.IdentifiesSameEntry(published)))
                || (intent != null
                    && !RegistrationCleanupIntentMatchesPublication(
                        intent,
                        Path.GetFileName(destination),
                        expectedPhysicalObjectIdentity)))
            {
                return false;
            }

            if (claim != null)
            {
                claim.Delete(immediateWindows: true);
                claim.Dispose();
                FlushFileMoveDirectory(
                    state,
                    "completed registration-publication claim retirement");
            }
            AfterRegistrationPublicationClaimRetiredForTest?.Invoke();
            if (intent != null
                && !DeletePinnedRegistrationCleanupIntent(state, intent))
            {
                return false;
            }

            state.Dispose();
            statePublication.RetirePinnedEmptyDirectoryFromNamespace(
                stateName);
            FlushFileMoveDirectory(
                parent,
                "completed registration-publication state retirement");
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Could not retire completed registration-publication state for {Destination}",
                LogRedaction.SanitizeFilePath(destination));
            return false;
        }
    }
}
