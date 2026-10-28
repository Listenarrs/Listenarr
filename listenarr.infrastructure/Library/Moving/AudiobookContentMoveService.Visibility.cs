namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static bool PinnedDirectoryVisibleOrThrowUnavailable(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        string unavailableMessage)
    {
        var outcome = directory.ProbeVisiblePathMatch();
        if (outcome == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(unavailableMessage);
        }

        return outcome == RegistrationPublicationMatchOutcome.Match;
    }

    private static bool PinnedFileVisibleOrThrowUnavailable(
        PinnedDirectoryCreation.PinnedFileEntry file,
        string unavailableMessage)
    {
        var outcome = file.ProbeVisiblePathMatch();
        if (outcome == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(unavailableMessage);
        }

        return outcome == RegistrationPublicationMatchOutcome.Match;
    }
}
