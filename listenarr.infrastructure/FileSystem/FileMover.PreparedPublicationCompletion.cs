using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private bool TryCompleteRecoveredPreparedPublication(
        PreparedPublicationRecoveryResult recovery,
        FileMoveGateLease lease,
        FileAction action,
        string sourceFile,
        string destinationFile,
        Action<IAudiobookFileRegistrationLease>? capturePublication)
    {
        using var recoveredDestination =
            lease.DestinationParent.TryOpenExistingFile(
                lease.DestinationName,
                requireDeleteAccess: false);
        if (!lease.DestinationParent.VisiblePathMatches()
            || recoveredDestination == null
            || !recoveredDestination.VisiblePathMatches()
            || string.IsNullOrWhiteSpace(
                recovery.PublishedDestinationObjectIdentity)
            || !string.Equals(
                recoveredDestination.GetObjectIdentity(),
                recovery.PublishedDestinationObjectIdentity,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (capturePublication != null)
        {
            if (string.IsNullOrWhiteSpace(recovery.SourceObjectIdentity))
            {
                throw new InvalidOperationException(
                    "Recovered file publication has no source generation evidence.");
            }
            CapturePublishedRegistrationLease(
                PinnedAudiobookFileRegistrationLease.Create(
                    recoveredDestination.OpenStableRegistrationCopy(),
                    destinationFile,
                    sourcePhysicalObjectIdentity:
                        recovery.SourceObjectIdentity),
                capturePublication);
        }

        LogMutation(
            FileMutationOutcome.Success,
            action,
            sourceFile,
            destinationFile,
            "Recovered a durable prepared-file publication");
        return true;
    }
}
