using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static FileMoveStatePaths GetFileMoveStatePaths(
        string sourceFile,
        string destinationFile,
        string sourceIdentity,
        string destinationIdentity)
    {
        var normalizedSource = Path.GetFullPath(sourceFile);
        var normalizedDestination = Path.GetFullPath(destinationFile);
        var token = HashPathIdentity($"{sourceIdentity}\0{destinationIdentity}");
        var sourceStateDirectory = Path.Join(
            Path.GetDirectoryName(normalizedSource)!,
            $".listenarr-file-source-{token}.state");
        var destinationStateDirectory = Path.Join(
            Path.GetDirectoryName(normalizedDestination)!,
            $".listenarr-file-destination-{token}.state");
        return new FileMoveStatePaths(
            sourceStateDirectory,
            destinationStateDirectory,
            Path.Join(sourceStateDirectory, "source.claim"),
            Path.Join(destinationStateDirectory, "destination.stage"),
            Path.Join(destinationStateDirectory, "destination.previous"),
            Path.Join(sourceStateDirectory, "operation.state"),
            Path.Join(sourceStateDirectory, "replacement-generation.fence"));
    }

    private static string HashPathIdentity(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24];

    private static bool TryValidateStateDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        var mode = File.GetUnixFileMode(path);
        var disallowed = UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        return (mode & disallowed) == 0;
    }

    private static PinnedDirectoryCreation CreateAnchoredFileMoveStateDirectory(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string stateName)
    {
        var creation = parent.TryCreateChildForPublication(stateName);
        try
        {
            if (!creation.Created || !creation.VisiblePathMatches())
            {
                throw new IOException(
                    "The deterministic file-move state directory is already occupied.");
            }
            creation.RestrictToCurrentUser();

            return creation;
        }
        catch
        {
            creation.Dispose();
            throw;
        }
    }

    private static bool AnchoredStateContainsOnly(
        PinnedDirectoryCreation.PinnedDirectoryAnchor state,
        params string[] allowedNames)
    {
        if (!state.VisiblePathMatches())
        {
            return false;
        }
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var allowed = allowedNames.ToHashSet(comparer);
        var actual = Directory.EnumerateFileSystemEntries(state.FullPath)
            .Select(Path.GetFileName)
            .ToList();
        return state.VisiblePathMatches()
            && actual.All(name => name != null && allowed.Contains(name));
    }

    private static void TryDeleteAnchoredStateDirectory(
        PinnedDirectoryCreation? publication,
        string stateName)
    {
        if (publication == null)
        {
            return;
        }
        try
        {
            publication.RetirePinnedEmptyDirectoryFromNamespace(
                stateName);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or InvalidOperationException)
        {
            // Preserve non-empty or changed recovery state.
        }
    }
}
