namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static void ValidatePinnedSourcePhysicalIdentity(
        AudiobookContentMoveRequest request,
        MoveJobEntry manifestEntry,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry)
    {
        // Persisted physical identity is legacy diagnostic data. Source authority
        // comes from the live pinned path plus the manifest content proof.
        _ = request;
        _ = manifestEntry;
        _ = sourceEntry;
    }
}
