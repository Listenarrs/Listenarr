namespace Listenarr.Infrastructure.FileSystem;

// Process-local observations detect substitutions where native identity is available.
// They own no handles and are never durable permission to delete. Each consumer must
// reopen under its authorized boundary and verify current content and visibility.
internal sealed record FilePublicationObservation(string? ObjectIdentity)
{
    internal static FilePublicationObservation Capture(
        PinnedDirectoryCreation.PinnedFileEntry entry)
    {
        if (!entry.IsRegularFile()
            || entry.ProbePublicPathMatch() != RegistrationPublicationMatchOutcome.Match)
        {
            throw new IOException("The file publication changed while it was observed.");
        }

        var identity = PinnedDirectoryCreation.CaptureDiagnosticIdentity(entry.GetObjectIdentity);
        return new(string.IsNullOrEmpty(identity) ? null : identity);
    }

    internal bool Matches(PinnedDirectoryCreation.PinnedFileEntry entry) =>
        entry.IsRegularFile()
        && entry.ProbePublicPathMatch() == RegistrationPublicationMatchOutcome.Match
        && (ObjectIdentity == null || entry.MatchesObjectIdentity(ObjectIdentity));
}
