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
                out var expectedIdentity)
            || string.IsNullOrWhiteSpace(expectedIdentity))
        {
            throw new MoveNeedsAttentionException(
                $"The move request has no physical source identity for tracked file: {manifestEntry.RelativePath}");
        }

        if (!string.Equals(
                sourceEntry.GetObjectIdentity(),
                expectedIdentity,
                StringComparison.Ordinal))
        {
            throw new MoveNeedsAttentionException(
                $"The tracked source file identifies a different physical generation: {manifestEntry.RelativePath}");
        }
    }
}
