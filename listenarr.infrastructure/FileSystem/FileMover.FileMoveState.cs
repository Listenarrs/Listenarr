using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static string HashPathIdentity(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24];

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
        var allowed = allowedNames.ToHashSet(DurableRecoveryArtifactNameComparer);
        var actual = Directory.EnumerateFileSystemEntries(state.FullPath)
            .Select(Path.GetFileName)
            .ToList();
        return state.VisiblePathMatches()
            && actual.All(name => name != null && allowed.Contains(name));
    }
}
