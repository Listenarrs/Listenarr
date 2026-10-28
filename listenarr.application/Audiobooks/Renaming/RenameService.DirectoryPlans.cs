namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private static HashSet<int> GetTrackedFileIdsForFolderChange(
        Audiobook audiobook)
    {
        if (audiobook.Files is { Count: > 0 })
        {
            return audiobook.Files
                .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                .Select(file => file.Id)
                .ToHashSet();
        }

        return !string.IsNullOrWhiteSpace(audiobook.FilePath)
            ? [0]
            : [];
    }
}
