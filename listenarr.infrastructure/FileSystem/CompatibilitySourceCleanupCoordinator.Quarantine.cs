using System.Text.Json;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class CompatibilitySourceCleanupCoordinator
{
    private void PrepareQuarantineDirectory(string path, Guid batchId)
    {
        var markerPath = Path.Join(path, OwnershipMarkerName);
        var marker = JsonSerializer.Serialize(new
        {
            ProtocolVersion = CompatibilityFilePublicationProtocol.Current,
            BatchId = batchId
        });
        var existed = Directory.Exists(path);
        if (existed
            && (!File.Exists(markerPath)
                || !string.Equals(
                    File.ReadAllText(markerPath),
                    marker,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "An existing quarantine directory is not owned by this batch.");
        }

        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var actual = File.GetUnixFileMode(path);
            var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((actual & forbidden) != 0)
            {
                throw new UnauthorizedAccessException(
                    "The quarantine directory permissions are not private.");
            }
        }
        else
        {
            ApplyPrivateWindowsAcl(path);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }

        if (!File.Exists(markerPath))
        {
            File.WriteAllText(markerPath, marker);
        }
        else if (!string.Equals(File.ReadAllText(markerPath), marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The quarantine ownership marker does not match this batch.");
        }
    }

    private static bool ContentMatches(string path, long length, string sha256)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parentPath = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parentPath);
            var outcome = parent.TryOpenExistingFileWithOutcome(
                fileName,
                requireDeleteAccess: false,
                out var openedFile);
            using var file = openedFile;
            if (outcome != PinnedFileOpenOutcome.Opened
                || file == null
                || !file.IsRegularFile()
                || !file.MatchesAsync(length, sha256, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult())
            {
                return false;
            }

            return parent.VisiblePathMatches() && file.VisiblePathMatches();
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}
