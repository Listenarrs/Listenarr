namespace Listenarr.Infrastructure.FileSystem;

public sealed class FileSystemVolumeResolver : IFileSystemVolumeResolver
{
    public FileSystemVolumeComparison Compare(
        string sourcePath,
        string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            var sourceBoundary = FindNearestExistingDirectory(sourcePath);
            var destinationBoundary = FindNearestExistingDirectory(destinationPath);
            if (sourceBoundary == null || destinationBoundary == null)
            {
                return new FileSystemVolumeComparison(
                    IsAvailable: false,
                    SameVolume: false,
                    SourceBoundary: sourceBoundary,
                    DestinationBoundary: destinationBoundary,
                    Reason: "No existing source or destination ancestor was available for volume comparison.");
            }

            using var source = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                sourceBoundary);
            using var destination = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                destinationBoundary);
            return new FileSystemVolumeComparison(
                IsAvailable: true,
                SameVolume: source.IsOnSameVolume(destination),
                SourceBoundary: sourceBoundary,
                DestinationBoundary: destinationBoundary);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or InvalidOperationException or PlatformNotSupportedException
                or ArgumentException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return new FileSystemVolumeComparison(
                IsAvailable: false,
                SameVolume: false,
                SourceBoundary: null,
                DestinationBoundary: null,
                Reason: exception.Message);
        }
    }

    private static string? FindNearestExistingDirectory(string path)
    {
        var current = Path.GetFullPath(path);
        if (File.Exists(current))
        {
            current = Path.GetDirectoryName(current) ?? current;
        }

        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return null;
            }
            current = parent;
        }

        return current;
    }
}
