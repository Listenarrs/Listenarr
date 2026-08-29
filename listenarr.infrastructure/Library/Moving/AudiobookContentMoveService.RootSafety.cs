namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static void ValidateMoveSourceRoot(string source)
    {
        ValidateMoveRootPath(source, mustExist: true, "source");
    }

    private static void ValidateMoveTargetRoot(string target)
    {
        ValidateMoveRootPath(target, mustExist: false, "target");
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
        var fullPathExists = TryGetMarkerlessPathAttributes(
            fullPath,
            out var fullPathAttributes);
        if (fullPathExists
            && (fullPathAttributes & FileAttributes.Directory) == 0)
        {
            throw new MoveNeedsAttentionException(
                $"The move {purpose} path is occupied by a file.");
        }

        if (mustExist && !fullPathExists)
        {
            throw new MoveNeedsAttentionException(
                $"The move {purpose} directory does not exist.");
        }

        var nearestExistingPath = fullPath;
        var nearestExistingAttributes = fullPathAttributes;
        while (!fullPathExists)
        {
            var parent = Path.GetDirectoryName(nearestExistingPath);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, nearestExistingPath, StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    $"The move {purpose} path has no existing directory ancestor.");
            }

            nearestExistingPath = parent;
            fullPathExists = TryGetMarkerlessPathAttributes(
                nearestExistingPath,
                out nearestExistingAttributes);
        }

        if ((nearestExistingAttributes & FileAttributes.Directory) == 0)
        {
            throw new MoveNeedsAttentionException(
                $"The move {purpose} path is blocked by a file ancestor.");
        }

        var current = nearestExistingPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (!TryGetMarkerlessPathAttributes(current, out var attributes))
            {
                throw new MoveNeedsAttentionException(
                    $"The move {purpose} path changed while its ancestor chain was being validated.");
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new MoveNeedsAttentionException(
                    $"The move {purpose} path traverses a symbolic link or reparse point.");
            }
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new MoveNeedsAttentionException(
                    $"The move {purpose} path is blocked by a file ancestor.");
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
