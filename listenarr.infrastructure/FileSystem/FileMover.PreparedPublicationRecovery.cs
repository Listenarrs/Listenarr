namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<PinnedDirectoryCreation.PinnedFileEntry?>
        RecoverCommittedPreparedPublicationAsync(
            PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
            string destinationName,
            PinnedDirectoryCreation.PinnedDirectoryAnchor state,
            PinnedDirectoryCreation.PinnedFileEntry operationState,
            PreparedPublicationState evidence,
            FileMoveContent expectedContent,
            PinnedDirectoryCreation.PinnedFileEntry? prepared,
            PinnedDirectoryCreation.PinnedFileEntry? previous,
            PinnedDirectoryCreation.PinnedFileEntry? publicationClaim,
            PinnedDirectoryCreation.PinnedFileEntry? destination)
    {
        if (prepared != null)
        {
            if (destination != null
                || publicationClaim == null
                || !publicationClaim.IdentifiesSameEntry(prepared))
            {
                throw new IOException(
                    "Committed file publication cannot prove its unpublished prepared generation.");
            }

            prepared.MoveTo(destinationParent, destinationName);
            FlushFileMoveDirectory(
                destinationParent,
                "interrupted prepared-generation publication");
            FlushFileMoveDirectory(
                state,
                "interrupted prepared-claim retirement");
            destination = destinationParent.TryOpenExistingFile(
                destinationName,
                requireDeleteAccess: false);
            if (destination == null
                || !prepared.VisiblePathMatches()
                || !destination.VisiblePathMatches()
                || !prepared.IdentifiesSameEntry(destination)
                || !publicationClaim.IdentifiesSameEntry(destination))
            {
                throw new IOException(
                    "The prepared generation changed while publication was recovered.");
            }
        }

        if (destination == null
            || !destination.VisiblePathMatches()
            || !await FileMatchesMoveContentAsync(
                destination,
                expectedContent))
        {
            throw new IOException(
                "Committed file publication has no matching public destination generation.");
        }

        var publishedIdentity = destination.GetObjectIdentity();
        if (publicationClaim != null)
        {
            if (!publicationClaim.VisiblePathMatches()
                || !publicationClaim.IdentifiesSameEntry(destination)
                || (!string.IsNullOrWhiteSpace(
                        evidence.PublishedDestinationObjectIdentity)
                    && !string.Equals(
                        evidence.PublishedDestinationObjectIdentity,
                        publishedIdentity,
                        StringComparison.Ordinal)))
            {
                throw new IOException(
                    "The destination publication claim does not prove the public generation.");
            }

            evidence = evidence with
            {
                PublishedDestinationObjectIdentity = publishedIdentity
            };
            WritePreparedPublicationState(operationState, evidence);
            FlushFileMoveDirectory(
                state,
                "recovered public destination generation evidence");
        }
        else if (string.IsNullOrWhiteSpace(
                evidence.PublishedDestinationObjectIdentity)
            || !string.Equals(
                evidence.PublishedDestinationObjectIdentity,
                publishedIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The committed public destination generation is unproven.");
        }

        if (previous != null)
        {
            previous.Delete(immediateWindows: true);
            FlushFileMoveDirectory(
                state,
                "previous destination recovery retirement");
        }

        if (publicationClaim != null)
        {
            if (!publicationClaim.VisiblePathMatches()
                || !destination.VisiblePathMatches()
                || !publicationClaim.IdentifiesSameEntry(destination))
            {
                throw new IOException(
                    "The public destination changed before publication-claim retirement.");
            }
            publicationClaim.Delete(immediateWindows: true);
            FlushFileMoveDirectory(
                state,
                "destination publication-claim retirement");
        }

        if (!destination.VisiblePathMatches()
            || !string.Equals(
                destination.GetObjectIdentity(),
                evidence.PublishedDestinationObjectIdentity,
                StringComparison.Ordinal)
            || !await FileMatchesMoveContentAsync(
                destination,
                expectedContent))
        {
            throw new IOException(
                "The public destination changed before publication recovery committed.");
        }

        return destination;
    }
}
