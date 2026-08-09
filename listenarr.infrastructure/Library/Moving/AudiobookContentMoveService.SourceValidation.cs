using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const string RootManifestRelativePath = "";

    private sealed record ValidatedSourceEntry(
        string FullPath,
        string RelativePath,
        bool IsDirectory,
        DateTime LastWriteTimeUtc);

    private static IReadOnlyList<ValidatedSourceEntry> ValidateSourceTreeForMove(
        string source,
        string target,
        bool targetInsideSource,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? structuralSpinePaths = null)
    {
        if (!Directory.Exists(source))
        {
            throw new MoveNeedsAttentionException("The move source directory does not exist.");
        }

        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException("Move sources cannot be symlinks or reparse points.");
        }

        var entries = new List<ValidatedSourceEntry>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(source);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (targetInsideSource && IsSameOrInside(entry, target, sourceSemantics))
                {
                    continue;
                }

                var isStructuralSpine = structuralSpinePaths?.Any(path =>
                    FileSystemPathIdentity.AreEquivalent(path, entry, sourceSemantics)) == true;
                if (isStructuralSpine)
                {
                    if (!Directory.Exists(entry))
                    {
                        throw new MoveNeedsAttentionException(
                            "A target structural directory became a file.");
                    }

                    continue;
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new MoveNeedsAttentionException(
                        $"Move entry '{Path.GetRelativePath(source, entry)}' is a symlink or reparse point.");
                }

                if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                        source,
                        entry,
                        sourceSemantics,
                        out var relativePath))
                {
                    throw new MoveNeedsAttentionException("A source entry escaped the source root.");
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                entries.Add(new ValidatedSourceEntry(
                    entry,
                    relativePath,
                    isDirectory,
                    isDirectory
                        ? Directory.GetLastWriteTimeUtc(entry)
                        : File.GetLastWriteTimeUtc(entry)));
                if (isDirectory)
                {
                    pendingDirectories.Push(entry);
                }
            }
        }

        return entries;
    }

    private static bool IsRootManifestEntry(MoveJobEntry entry) =>
        entry.EntryType == MoveJobEntryType.Directory
        && string.Equals(
            entry.RelativePath,
            RootManifestRelativePath,
            StringComparison.Ordinal);

}
