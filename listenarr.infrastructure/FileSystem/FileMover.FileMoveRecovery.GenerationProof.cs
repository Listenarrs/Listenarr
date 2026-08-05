namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static bool RecoveryArtifactsMatchPersistedGeneration(
        FileMoveFence? persistedFence,
        PinnedDirectoryCreation.PinnedFileEntry? sourceClaim,
        PinnedDirectoryCreation.PinnedFileEntry? destinationStage,
        PinnedDirectoryCreation.PinnedFileEntry? destinationPrevious,
        PinnedDirectoryCreation.PinnedFileEntry? destinationPublicationClaim,
        bool sourceRetirementCommitted)
    {
        if (sourceClaim != null
            && (persistedFence is not { Version: >= 2 } sourceFence
                || string.IsNullOrWhiteSpace(sourceFence.SourceObjectIdentity)
                || !string.Equals(
                    sourceClaim.GetObjectIdentity(),
                    sourceFence.SourceObjectIdentity,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        if (destinationStage != null
            && (persistedFence is not { Version: >= 2 } stageFence
                || string.IsNullOrWhiteSpace(stageFence.DestinationStageObjectIdentity)
                || !string.Equals(
                    destinationStage.GetObjectIdentity(),
                    stageFence.DestinationStageObjectIdentity,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        if (destinationPrevious != null
            && (persistedFence is not { Version: >= 2 } previousFence
                || string.IsNullOrWhiteSpace(
                    previousFence.DestinationPreviousObjectIdentity)
                || !string.Equals(
                    destinationPrevious.GetObjectIdentity(),
                    previousFence.DestinationPreviousObjectIdentity,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        if (destinationPublicationClaim == null)
        {
            return true;
        }

        if (persistedFence is not { Version: >= 2 } publicationFence
            || !destinationPublicationClaim.VisiblePathMatches())
        {
            return false;
        }

        var publicationSource = publicationFence.NativeRename
            ? sourceClaim
            : destinationStage;
        if (publicationSource != null
            && !destinationPublicationClaim.IdentifiesSameEntry(publicationSource))
        {
            return false;
        }

        return sourceRetirementCommitted || publicationSource != null;
    }
}
