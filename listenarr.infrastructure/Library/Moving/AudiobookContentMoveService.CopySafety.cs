namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static async Task<bool> FileMatchesManifestAsync(
        string path,
        MoveJobEntry manifestEntry,
        CancellationToken cancellationToken)
    {
        var exists = TryGetMarkerlessPathAttributes(path, out var attributes);
        if (!exists
            || manifestEntry.EntryType != MoveJobEntryType.File
            || (attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0
            || new FileInfo(path).Length != manifestEntry.Length
            || string.IsNullOrWhiteSpace(manifestEntry.Sha256))
        {
            return false;
        }

        return string.Equals(
            await ComputeSha256Async(path, cancellationToken),
            manifestEntry.Sha256,
            StringComparison.Ordinal);
    }

}
