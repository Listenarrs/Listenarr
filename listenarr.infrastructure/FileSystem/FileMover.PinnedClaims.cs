namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<PreparedPublicationRecoveryResult>
        RecoverPreparedFilePublicationAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string destinationName,
        string stateName)
    {
        using var statePublication =
            destinationParent.TryOpenExistingChildForPublication(stateName);
        if (statePublication == null)
        {
            return new PreparedPublicationRecoveryResult(
                PreparedPublicationRecoveryOutcome.None);
        }

        using var state = statePublication.OpenCreatedDirectoryAnchor();
        if (!destinationParent.VisiblePathMatches()
            || !state.VisiblePathMatches()
            || !AnchoredStateContainsOnly(
                state,
                "operation.state",
                "prepared.claim",
                "destination.previous",
                "destination.published.claim",
                "publication.fence"))
        {
            throw new IOException(
                "Recoverable file-publication state contains unsafe entries.");
        }

        var operationState = state.TryOpenExistingFile(
            "operation.state",
            requireDeleteAccess: true);
        var prepared = state.TryOpenExistingFile(
            "prepared.claim",
            requireDeleteAccess: true);
        var previous = state.TryOpenExistingFile(
            "destination.previous",
            requireDeleteAccess: true);
        var publicationClaim = state.TryOpenExistingFile(
            "destination.published.claim",
            requireDeleteAccess: true);
        var legacyFence = state.TryOpenExistingFile(
            "publication.fence",
            requireDeleteAccess: true);
        PinnedDirectoryCreation.PinnedFileEntry? destination = null;
        var recoveryResult = new PreparedPublicationRecoveryResult(
            PreparedPublicationRecoveryOutcome.RolledBack);
        try
        {
            if (operationState == null || legacyFence != null)
            {
                throw new IOException(
                    "Prepared file-publication state lacks current durable generation evidence.");
            }

            var evidence = ReadPreparedPublicationState(operationState);
            if (evidence == null
                || !string.Equals(
                    evidence.DestinationName,
                    destinationName,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "Prepared file-publication generation evidence is invalid.");
            }

            var expectedContent = new FileMoveContent(
                evidence.PreparedLength,
                evidence.PreparedSha256);
            if (prepared != null
                && (!prepared.VisiblePathMatches()
                    || !string.Equals(
                        prepared.GetObjectIdentity(),
                        evidence.PreparedObjectIdentity,
                        StringComparison.Ordinal)
                    || !await FileMatchesMoveContentAsync(
                        prepared,
                        expectedContent)))
            {
                throw new IOException(
                    "The prepared publication claim changed before recovery.");
            }
            if (previous != null
                && (!previous.VisiblePathMatches()
                    || !string.Equals(
                        previous.GetObjectIdentity(),
                        evidence.PreviousObjectIdentity,
                        StringComparison.Ordinal)))
            {
                throw new IOException(
                    "The previous destination generation changed before recovery.");
            }

            destination = destinationParent.TryOpenExistingFile(
                destinationName,
                requireDeleteAccess: false);
            if (publicationClaim != null)
            {
                if (!publicationClaim.VisiblePathMatches())
                {
                    throw new IOException(
                        "The destination publication claim changed before recovery.");
                }

                var claimedGeneration = prepared ?? destination;
                if (claimedGeneration == null
                    || !publicationClaim.IdentifiesSameEntry(claimedGeneration))
                {
                    throw new IOException(
                        "The destination publication claim no longer proves the recoverable generation.");
                }
            }

            if (!evidence.Committed)
            {
                RecoverUncommittedPreparedPublication(
                    destinationParent,
                    destinationName,
                    state,
                    evidence,
                    ref prepared,
                    ref previous,
                    ref publicationClaim,
                    destination);
            }
            else
            {
                destination = await RecoverCommittedPreparedPublicationAsync(
                    destinationParent,
                    destinationName,
                    state,
                    operationState,
                    evidence,
                    expectedContent,
                    prepared,
                    previous,
                    publicationClaim,
                    destination);
                var completedEvidence = ReadPreparedPublicationState(
                    operationState);
                if (completedEvidence == null
                    || string.IsNullOrWhiteSpace(
                        completedEvidence.PublishedDestinationObjectIdentity))
                {
                    throw new IOException(
                        "Completed prepared publication lacks durable public generation evidence.");
                }
                recoveryResult = new PreparedPublicationRecoveryResult(
                    PreparedPublicationRecoveryOutcome.Completed,
                    completedEvidence.PublishedDestinationObjectIdentity,
                    completedEvidence.SourceObjectIdentity);
            }

            operationState.Delete(immediateWindows: true);
            operationState.Dispose();
            operationState = null;
            FlushFileMoveDirectory(
                state,
                "prepared publication operation-state retirement");
        }
        finally
        {
            destination?.Dispose();
            legacyFence?.Dispose();
            publicationClaim?.Dispose();
            previous?.Dispose();
            prepared?.Dispose();
            operationState?.Dispose();
        }

        state.Dispose();
        statePublication.RetirePinnedEmptyDirectoryFromNamespace(
            stateName);
        FlushFileMoveDirectory(
            destinationParent,
            "file-publication state retirement");
        return recoveryResult;
    }

    private void RecoverUncommittedPreparedPublication(
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string destinationName,
        PinnedDirectoryCreation.PinnedDirectoryAnchor state,
        PreparedPublicationState evidence,
        ref PinnedDirectoryCreation.PinnedFileEntry? prepared,
        ref PinnedDirectoryCreation.PinnedFileEntry? previous,
        ref PinnedDirectoryCreation.PinnedFileEntry? publicationClaim,
        PinnedDirectoryCreation.PinnedFileEntry? destination)
    {
        if (previous == null)
        {
            if (prepared != null
                || publicationClaim != null
                || destination == null
                || !destination.VisiblePathMatches()
                || !string.Equals(
                    destination.GetObjectIdentity(),
                    evidence.PreviousObjectIdentity,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "Interrupted file publication has incomplete pre-commit generation evidence.");
            }
            return;
        }

        if (destination != null)
        {
            throw new IOException(
                "Interrupted file publication has ambiguous pre-commit destination state.");
        }

        if (publicationClaim != null)
        {
            if (prepared == null
                || !publicationClaim.IdentifiesSameEntry(prepared))
            {
                throw new IOException(
                    "Interrupted publication claim does not match the prepared generation.");
            }
            publicationClaim.Delete(immediateWindows: true);
            publicationClaim.Dispose();
            publicationClaim = null;
            FlushFileMoveDirectory(
                state,
                "uncommitted publication-claim retirement");
        }

        if (prepared != null)
        {
            prepared.Delete(immediateWindows: true);
            prepared.Dispose();
            prepared = null;
            FlushFileMoveDirectory(
                state,
                "uncommitted prepared-generation retirement");
        }

        previous.MoveTo(destinationParent, destinationName);
        if (!previous.VisiblePathMatches()
            || !string.Equals(
                previous.GetObjectIdentity(),
                evidence.PreviousObjectIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The previous destination changed while it was being restored.");
        }
        previous.Dispose();
        previous = null;
        FlushFileMoveDirectory(
            destinationParent,
            "interrupted destination restoration");
        FlushFileMoveDirectory(
            state,
            "interrupted previous-generation retirement");
    }

    private async Task PublishPreparedFileReplacingCapturedDestinationAsync(
        PinnedDirectoryCreation.PinnedFileEntry prepared,
        string sourceObjectIdentity,
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string destinationName,
        PinnedDirectoryCreation.PinnedFileEntry capturedDestination,
        string stateName,
        Action transferPreparedOwnership)
    {
        using var statePublication = CreateAnchoredFileMoveStateDirectory(
            destinationParent,
            stateName);
        using var state = statePublication.OpenCreatedDirectoryAnchor();
        FlushFileMoveDirectory(
            destinationParent,
            "file-publication state creation");

        var preparedContent = await CaptureFileMoveContentAsync(prepared);
        var evidence = new PreparedPublicationState(
            PreparedPublicationStateVersion,
            destinationName,
            sourceObjectIdentity,
            prepared.GetObjectIdentity(),
            capturedDestination.GetObjectIdentity(),
            preparedContent.Length,
            preparedContent.Sha256,
            Committed: false,
            PublishedDestinationObjectIdentity: null);
        using var operationState = state.CreateNewFile("operation.state");
        WritePreparedPublicationState(operationState, evidence);
        FlushFileMoveDirectory(
            state,
            "prepared publication generation evidence");

        capturedDestination.MoveTo(state, "destination.previous");
        if (!capturedDestination.VisiblePathMatches()
            || !string.Equals(
                capturedDestination.GetObjectIdentity(),
                evidence.PreviousObjectIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The captured destination changed while it was quarantined.");
        }
        FlushFileMoveDirectory(
            destinationParent,
            "captured destination retirement");
        FlushFileMoveDirectory(
            state,
            "captured destination publication");
        if (AfterPreparedDestinationCapturedForTestAsync != null)
        {
            await AfterPreparedDestinationCapturedForTestAsync();
        }

        prepared.MoveTo(state, "prepared.claim");
        transferPreparedOwnership();
        if (!prepared.VisiblePathMatches()
            || !await FileMatchesMoveContentAsync(
                prepared,
                preparedContent))
        {
            throw new IOException(
                "The prepared generation changed while it was claimed.");
        }
        if (AfterPreparedClaimMovedBeforeEvidenceForTestAsync != null)
        {
            await AfterPreparedClaimMovedBeforeEvidenceForTestAsync();
        }
        evidence = evidence with
        {
            PreparedObjectIdentity = prepared.GetObjectIdentity()
        };
        WritePreparedPublicationState(operationState, evidence);
        FlushFileMoveDirectory(
            destinationParent,
            "prepared generation retirement");
        FlushFileMoveDirectory(
            state,
            "prepared generation claim");
        if (AfterPreparedClaimPublishedForTestAsync != null)
        {
            await AfterPreparedClaimPublishedForTestAsync();
        }

        using var publicationClaim = prepared.CreateHardLinkTo(
            state,
            "destination.published.claim");
        if (!publicationClaim.VisiblePathMatches()
            || !publicationClaim.IdentifiesSameEntry(prepared))
        {
            throw new IOException(
                "The prepared generation could not establish a durable publication claim.");
        }
        evidence = evidence with
        {
            PreparedObjectIdentity = prepared.GetObjectIdentity()
        };
        WritePreparedPublicationState(operationState, evidence);
        FlushFileMoveDirectory(
            state,
            "destination publication generation claim");

        evidence = evidence with { Committed = true };
        WritePreparedPublicationState(operationState, evidence);
        FlushFileMoveDirectory(
            state,
            "file-publication commit state");
        if (AfterPreparedPublicationCommittedForTestAsync != null)
        {
            await AfterPreparedPublicationCommittedForTestAsync();
        }

        using var appearedDestination = destinationParent.TryOpenExistingFile(
            destinationName,
            requireDeleteAccess: false);
        if (appearedDestination != null)
        {
            throw new IOException(
                "The destination was recreated after its captured generation was quarantined.");
        }

        prepared.MoveTo(destinationParent, destinationName);
        FlushFileMoveDirectory(
            destinationParent,
            "prepared generation publication");
        FlushFileMoveDirectory(
            state,
            "prepared generation claim retirement");
        if (AfterPreparedDestinationPublishedForTestAsync != null)
        {
            await AfterPreparedDestinationPublishedForTestAsync();
        }
        using var publishedDestination = destinationParent.TryOpenExistingFile(
            destinationName,
            requireDeleteAccess: false);
        if (publishedDestination == null
            || !prepared.VisiblePathMatches()
            || !publishedDestination.VisiblePathMatches()
            || !publicationClaim.VisiblePathMatches()
            || !prepared.IdentifiesSameEntry(publishedDestination)
            || !publicationClaim.IdentifiesSameEntry(publishedDestination)
            || !await FileMatchesMoveContentAsync(
                publishedDestination,
                preparedContent))
        {
            throw new IOException(
                "The prepared generation changed while it was published.");
        }

        evidence = evidence with
        {
            PublishedDestinationObjectIdentity =
                publishedDestination.GetObjectIdentity()
        };
        WritePreparedPublicationState(operationState, evidence);
        FlushFileMoveDirectory(
            state,
            "published destination generation evidence");

        if (!capturedDestination.VisiblePathMatches()
            || !string.Equals(
                capturedDestination.GetObjectIdentity(),
                evidence.PreviousObjectIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The previous destination changed before retirement.");
        }
        capturedDestination.Delete(immediateWindows: true);
        capturedDestination.Dispose();
        FlushFileMoveDirectory(
            state,
            "captured destination retirement");

        if (!publicationClaim.VisiblePathMatches()
            || !publishedDestination.VisiblePathMatches()
            || !publicationClaim.IdentifiesSameEntry(publishedDestination))
        {
            throw new IOException(
                "The public destination changed before publication-claim retirement.");
        }
        publicationClaim.Delete(immediateWindows: true);
        publicationClaim.Dispose();
        FlushFileMoveDirectory(
            state,
            "destination publication-claim retirement");

        if (!publishedDestination.VisiblePathMatches()
            || !string.Equals(
                publishedDestination.GetObjectIdentity(),
                evidence.PublishedDestinationObjectIdentity,
                StringComparison.Ordinal)
            || !await FileMatchesMoveContentAsync(
                publishedDestination,
                preparedContent))
        {
            throw new IOException(
                "The public destination changed before publication state retirement.");
        }

        operationState.Delete(immediateWindows: true);
        operationState.Dispose();
        FlushFileMoveDirectory(
            state,
            "file-publication operation-state retirement");

        state.Dispose();
        statePublication.RetirePinnedEmptyDirectoryFromNamespace(
            stateName);
        FlushFileMoveDirectory(
            destinationParent,
            "file-publication state retirement");
    }
}
