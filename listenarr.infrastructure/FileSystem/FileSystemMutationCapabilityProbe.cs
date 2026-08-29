namespace Listenarr.Infrastructure.FileSystem;

internal static class FileSystemMutationCapabilityProbe
{
    public static bool? ProbeReadOnlyNearestDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var current = Directory.Exists(fullPath)
                ? fullPath
                : Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current))
                {
                    return ProbeReadOnlyDirectory(current);
                }

                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }

        return null;
    }

    public static bool? ProbeReadOnlyDirectory(string directoryPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            using var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(directoryPath);
            if (!boundary.VisiblePathMatches())
            {
                return null;
            }

            return boundary.IsLinuxFileSystemReadOnly();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
