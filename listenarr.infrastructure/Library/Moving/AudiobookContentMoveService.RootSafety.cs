namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static void ValidateMoveSourceRoot(string source)
    {
        ValidateMoveRootPath(source, mustExist: true, "source");
    }

    private static void ValidateMoveTargetRoot(string target)
    {
        var fullTarget = Path.GetFullPath(target);
        if (File.Exists(fullTarget) && !Directory.Exists(fullTarget))
        {
            throw new MoveNeedsAttentionException(
                "The move target path is occupied by a file.");
        }

        ValidateMoveRootPath(fullTarget, mustExist: false, "target");
    }

    private static void ValidateExistingMoveDirectory(string directory, string purpose)
    {
        ValidateMoveRootPath(directory, mustExist: true, purpose);
    }

    private static void ValidateMoveRootPath(
        string path,
        bool mustExist,
        string purpose)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new MoveNeedsAttentionException(
                $"The move {purpose} path is occupied by a file.");
        }

        if (mustExist && !Directory.Exists(fullPath))
        {
            throw new MoveNeedsAttentionException(
                $"The move {purpose} directory does not exist.");
        }

        var nearestExistingPath = fullPath;
        while (!Directory.Exists(nearestExistingPath))
        {
            var parent = Path.GetDirectoryName(nearestExistingPath);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, nearestExistingPath, StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    $"The move {purpose} path has no existing directory ancestor.");
            }

            nearestExistingPath = parent;
        }

        var current = nearestExistingPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new MoveNeedsAttentionException(
                    $"The move {purpose} path traverses a symbolic link or reparse point.");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }
    }
}
