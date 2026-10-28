using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    internal static IReadOnlyList<string> GetTargetStructuralSpine(
        string source,
        string target,
        FileSystemPathSemantics semantics)
    {
        if (!FileSystemPathIdentity.IsSameOrInside(target, source, semantics)
            || FileSystemPathIdentity.AreEquivalent(target, source, semantics))
        {
            return [];
        }

        var result = new Stack<string>();
        var current = Path.GetDirectoryName(target);
        while (!string.IsNullOrWhiteSpace(current)
            && !FileSystemPathIdentity.AreEquivalent(current, source, semantics))
        {
            result.Push(current);
            current = Path.GetDirectoryName(current);
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            throw new MoveNeedsAttentionException(
                "The nested move target does not share the expected source boundary.");
        }

        return result.ToList();
    }

    internal static void ValidateExistingTargetSpine(
        IReadOnlyList<string> spine,
        string target,
        FileSystemPathSemantics semantics)
    {
        for (var index = 0; index < spine.Count; index++)
        {
            var directory = spine[index];
            if (!Directory.Exists(directory))
            {
                break;
            }

            ValidateExistingMoveDirectory(
                directory,
                "nested target structural directory");
            var expectedChild = index + 1 < spine.Count ? spine[index + 1] : target;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!FileSystemPathIdentity.AreEquivalent(
                        entry,
                        expectedChild,
                        semantics))
                {
                    throw new MoveNeedsAttentionException(
                        "A nested target structural directory contains unexpected content unrelated to the target path.");
                }
            }
        }
    }

    private static IReadOnlyList<string> FindMissingTargetAncestors(string targetParent)
    {
        var missing = new Stack<string>();
        var current = Path.GetFullPath(targetParent);
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new MoveNeedsAttentionException(
                    "A target ancestor is occupied by a file.");
            }

            missing.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new MoveNeedsAttentionException(
                    "No existing ancestor could be found for the move target.");
        }

        ValidateExistingMoveDirectory(current, "nearest existing target ancestor");
        return missing.ToList();
    }

    private static int GetPathDepth(string path) =>
        Path.GetFullPath(path)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Length;
}
