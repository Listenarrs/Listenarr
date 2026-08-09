using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Library;

public partial class RootFoldersController
{
    private async Task<RootFolderDto> MapAsync(RootFolder root)
    {
        RootFolderPathChangeResult? active = null;
        var relocation = await _relocationService.GetActiveForRootAsync(root.Id);
        if (relocation != null)
        {
            var relocationResult = await _relocationService.GetAsync(relocation.Id);
            active = relocationResult == null
                ? null
                : RootFolderRelocationPublicProjection.Sanitize(relocationResult);
        }

        var storage = await _storageHealthResolver.ResolveAsync(root);

        return new RootFolderDto(
            root.Id,
            root.Name,
            root.Path,
            FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                root.Path,
                out var pathSyntax)
                    ? pathSyntax.ToString()
                    : null,
            root.IsDefault,
            root.CaseSensitivityMode.ToString(),
            root.ResolvedCaseSensitivity.ToString(),
            root.PathIdentityState.ToString(),
            storage.State.ToString(),
            storage.Reason.ToString(),
            storage.Message,
            storage.CanConfirmCurrentFolder,
            storage.CanChangePath,
            storage.CanMutateFilesystem,
            storage.ConfirmationToken,
            root.CreatedAt,
            root.UpdatedAt,
            active);
    }
}
