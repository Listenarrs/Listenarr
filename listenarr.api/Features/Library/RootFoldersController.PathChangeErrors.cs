using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public partial class RootFoldersController
{
    private static ConflictObjectResult RootFolderPathChangeConflict(
        RootFolderPathChangeRejectedException exception) =>
        new(new
        {
            message = exception.PublicMessage,
            code = exception.Code
        });

    private static ConflictObjectResult RootFolderPathChangeBlocked() =>
        new(new
        {
            message = "The root folder path change is blocked by its current storage or recovery state. Refresh the root folder, resolve any active relocation or audiobook move recovery, and try again.",
            code = "root_folder_path_change_blocked"
        });
}
