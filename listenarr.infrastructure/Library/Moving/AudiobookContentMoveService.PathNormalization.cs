namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static string NormalizeMoveDirectoryEndpoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var trimmed = fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(
                trimmed,
                root.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return trimmed;
    }
}
