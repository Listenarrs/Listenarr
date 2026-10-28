namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task RetainMarkerlessSourceEntryAsync(
        AudiobookContentMoveRequest request,
        MoveJobEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.CleanupState != MoveJobEntryCleanupState.Retained)
        {
            await UpdateCleanupStateAsync(
                request.JobId,
                request.LeaseToken,
                entry.RelativePath,
                MoveJobEntryCleanupState.Retained,
                cancellationToken);
            entry.CleanupState = MoveJobEntryCleanupState.Retained;
        }
    }

    private static bool TryGetMarkerlessPathAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (System.ComponentModel.Win32Exception exception) when (
            OperatingSystem.IsWindows()
                ? exception.NativeErrorCode is 2 or 3
                : exception.NativeErrorCode == 2)
        {
            attributes = default;
            return false;
        }
    }

    private static void ValidateMarkerlessSourceDirectory(
        MoveJobEntry entry,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor originalDirectory)
    {
        if (!originalDirectory.VisiblePathMatches()
            || !directory.IdentifiesSameDirectory(originalDirectory)
            || !PinnedDirectoryVisibleOrThrowUnavailable(
                directory,
                $"A markerless source directory is temporarily unavailable: {entry.RelativePath}"))
        {
            throw new MoveNeedsAttentionException(
                $"A markerless source directory changed physical generation: {entry.RelativePath}");
        }
    }
}
