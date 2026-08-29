namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private RegistrationPublicationMatchOutcome ProbeCurrentPublication(
        IAudiobookFileRegistrationLease lease) =>
        RegistrationPublicationProbeForTest?.Invoke(lease)
        ?? (lease is IAudiobookFileRegistrationPublicationProbe probe
            ? probe.ProbeCurrentPublication()
            : lease.MatchesCurrentPublication()
                ? RegistrationPublicationMatchOutcome.Match
                : RegistrationPublicationMatchOutcome.Mismatch);

    private static bool VisiblePathMatchesOrThrowUnavailable(
        PinnedDirectoryCreation.PinnedFileEntry entry,
        string unavailableMessage)
    {
        var outcome = entry.ProbeVisiblePathMatch();
        if (outcome == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(unavailableMessage);
        }

        return outcome == RegistrationPublicationMatchOutcome.Match;
    }

    private static bool VisiblePathMatchesOrThrowUnavailable(
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
}
