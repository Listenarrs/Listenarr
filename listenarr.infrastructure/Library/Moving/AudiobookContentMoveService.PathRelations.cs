using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static bool IsSameOrInside(
        string candidate,
        string root,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedCandidate = Path.GetFullPath(candidate);
        var normalizedRoot = Path.GetFullPath(root);

        return FileSystemPathIdentity.IsSameOrInside(
            normalizedCandidate,
            normalizedRoot,
            semantics);
    }

    private static bool IsFilesystemRoot(
        string path,
        FileSystemPathSemantics? resolvedSemantics = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrWhiteSpace(root)
            && FileSystemPathIdentity.AreEquivalent(
                fullPath,
                root,
                resolvedSemantics
                    ?? throw new InvalidOperationException(
                        "Filesystem semantics are required for filesystem root checks."));
    }
}
