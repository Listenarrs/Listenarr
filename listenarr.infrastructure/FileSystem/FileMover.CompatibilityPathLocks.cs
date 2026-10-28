namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static FileMoveEndpoint? ResolveCompatibilityFileMoveEndpoint(
        string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!TryResolvePhysicalPath(fullPath, out var physical)
                || physical.EncounteredLink)
            {
                return null;
            }

            var resolvedPath = physical.ResolvedPath;
            return new FileMoveEndpoint(
                resolvedPath.ToUpperInvariant(),
                resolvedPath);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }
    }
}
