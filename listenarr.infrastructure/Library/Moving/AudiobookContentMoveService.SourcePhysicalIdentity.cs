namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static void ValidatePinnedSourcePhysicalIdentity(
        AudiobookContentMoveRequest request,
        MoveJobEntry manifestEntry,
        PinnedDirectoryCreation.PinnedFileEntry sourceEntry)
    {
        var identities = request.SourcePhysicalObjectIdentities;
        if (identities == null)
        {
            return;
        }

        if (!identities.TryGetValue(
                manifestEntry.RelativePath,
                out var expectedIdentity))
        {
            // Non-audio companion files are authorized by the exclusive managed
            // audiobook directory plus their immutable persisted content manifest.
            // Tracked audiobook files are present in this identity map and retain
            // the stronger physical-generation fence.
            return;
        }

        if (string.IsNullOrWhiteSpace(expectedIdentity)
            || !sourceEntry.MatchesObjectIdentity(expectedIdentity))
        {
            throw new MoveNeedsAttentionException(
                $"The tracked source file identifies a different physical generation: {manifestEntry.RelativePath}");
        }
    }
}
