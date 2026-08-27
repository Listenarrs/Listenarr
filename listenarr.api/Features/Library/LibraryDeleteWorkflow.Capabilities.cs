namespace Listenarr.Api.Features.Library;

public sealed record AudiobookDeleteCapabilities(
    bool CanRemoveFromLibrary,
    bool CanDeleteTrackedFiles,
    bool CanDeleteFolder,
    string? Reason,
    string FallbackAction);

public sealed partial class LibraryDeleteWorkflow
{
    public async Task<AudiobookDeleteCapabilities?> GetCapabilitiesAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _audiobookRepository.GetByIdSnapshotAsync(
            id,
            cancellationToken);
        if (snapshot == null)
        {
            return null;
        }

        var storageBlock = await GetManagedStorageMutationBlockAsync(
            snapshot,
            cancellationToken);
        var hasUnverifiedSource = HasUnverifiedTrackedDeleteSource(snapshot);
        var canDeleteFiles = storageBlock == null && !hasUnverifiedSource;
        var reason = storageBlock?.Message;
        if (reason == null && hasUnverifiedSource)
        {
            reason = "Tracked files do not expose durable identity for standalone deletion. Verified move cleanup requires a destination copy; removing the library record remains available.";
        }

        return new AudiobookDeleteCapabilities(
            CanRemoveFromLibrary: true,
            CanDeleteTrackedFiles: canDeleteFiles,
            CanDeleteFolder: canDeleteFiles
                && !string.IsNullOrWhiteSpace(snapshot.BasePath),
            Reason: reason,
            FallbackAction: "RemoveFromLibraryOnly");
    }
}
