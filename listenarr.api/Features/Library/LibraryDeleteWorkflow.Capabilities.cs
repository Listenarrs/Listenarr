using Listenarr.Application.Common.Exceptions;

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

        try
        {
            await _moveQueueService.EnsureFilesystemMutationAllowedAsync(
                id,
                cancellationToken,
                allowActiveDeletionIntent: true);
        }
        catch (ApplicationConflictException exception)
        {
            return new AudiobookDeleteCapabilities(
                CanRemoveFromLibrary: false,
                CanDeleteTrackedFiles: false,
                CanDeleteFolder: false,
                Reason: exception.SafeDetail,
                FallbackAction: "RemoveFromLibraryOnly");
        }

        var storageBlock = await GetManagedStorageMutationBlockAsync(
            snapshot,
            cancellationToken);
        var canDeleteFiles = storageBlock == null;
        var reason = storageBlock?.Message;

        return new AudiobookDeleteCapabilities(
            CanRemoveFromLibrary: true,
            CanDeleteTrackedFiles: canDeleteFiles,
            CanDeleteFolder: canDeleteFiles
                && !string.IsNullOrWhiteSpace(snapshot.BasePath),
            Reason: reason,
            FallbackAction: "RemoveFromLibraryOnly");
    }
}
