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
        FileAttributes sourceAttributes;
        try
        {
            sourceAttributes = File.GetAttributes(source);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            throw new MoveNeedsAttentionException("The move source directory does not exist.");
        }

        if ((sourceAttributes & FileAttributes.Directory) == 0)
        {
            throw new MoveNeedsAttentionException("The move source path is not a directory.");
        }
        if ((sourceAttributes & FileAttributes.ReparsePoint) != 0)
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
                    var structuralAttributes = File.GetAttributes(entry);
                    if ((structuralAttributes & FileAttributes.Directory) == 0)
                    {
                        throw new MoveNeedsAttentionException(
                            "A target structural directory became a file.");
                    }
                    if ((structuralAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new MoveNeedsAttentionException(
                            "A target structural directory became a link or reparse point.");
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

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor OpenPinnedMoveDescendant(
        AudiobookContentMoveRequest request,
        string endpointRoot,
        string directoryPath,
        FileSystemPathSemantics semantics,
        string expectedEndpointIdentity,
        bool sourceEndpoint)
    {
        var authorization = request.BoundaryAuthorization
            ?? throw new MoveNeedsAttentionException(
                "The move lacks loaded filesystem boundary authorization.");
        var boundaryPath = sourceEndpoint
            ? authorization.SourceBoundaryPath
            : authorization.TargetBoundaryPath;
        var boundaryIdentityVersion = sourceEndpoint
            ? authorization.SourceDirectoryObjectIdentityVersion
            : authorization.TargetDirectoryObjectIdentityVersion;
        var boundaryIdentity = sourceEndpoint
            ? authorization.SourceDirectoryObjectIdentity
            : authorization.TargetDirectoryObjectIdentity;

        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                endpointRoot,
                directoryPath,
                semantics,
                out var relativePath))
        {
            throw new MoveNeedsAttentionException(
                "A move descendant escaped its pinned endpoint.");
        }

        var current = OpenPinnedMoveBoundaryDescendant(
            boundaryPath,
            endpointRoot,
            semantics,
            boundaryIdentityVersion,
            boundaryIdentity,
            sourceEndpoint ? "source boundary" : "target boundary");
        try
        {
            if (!current.MatchesDirectoryObjectIdentity(expectedEndpointIdentity)
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    current,
                    "The move endpoint is temporarily unavailable while its physical generation is being verified."))
            {
                throw new MoveNeedsAttentionException(
                    "A move endpoint changed physical generation.");
            }

            foreach (var segment in SplitMovePathSegments(relativePath, semantics))
            {
                var next = OpenPinnedMoveChild(
                    current,
                    segment,
                    "move endpoint");
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor OpenPinnedMoveBoundaryDescendant(
        AudiobookContentMoveRequest request,
        string directoryPath,
        FileSystemPathSemantics semantics,
        bool sourceBoundary)
    {
        var authorization = request.BoundaryAuthorization
            ?? throw new MoveNeedsAttentionException(
                "The move lacks loaded filesystem boundary authorization.");
        return OpenPinnedMoveBoundaryDescendant(
            sourceBoundary
                ? authorization.SourceBoundaryPath
                : authorization.TargetBoundaryPath,
            directoryPath,
            semantics,
            sourceBoundary
                ? authorization.SourceDirectoryObjectIdentityVersion
                : authorization.TargetDirectoryObjectIdentityVersion,
            sourceBoundary
                ? authorization.SourceDirectoryObjectIdentity
                : authorization.TargetDirectoryObjectIdentity,
            sourceBoundary ? "source boundary" : "target boundary");
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor OpenPinnedMoveBoundaryDescendant(
        string boundaryPath,
        string directoryPath,
        FileSystemPathSemantics semantics,
        int boundaryIdentityVersion,
        string boundaryIdentity,
        string boundaryDescription)
    {
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                boundaryPath,
                directoryPath,
                semantics,
                out var relativePath))
        {
            throw new MoveNeedsAttentionException(
                $"A move directory escaped its authorized {boundaryDescription}.");
        }

        var current = PinnedDirectoryCreation.OpenPinnedBoundary(boundaryPath);
        try
        {
            if (!current.MatchesManagedDirectoryIdentity(
                    boundaryIdentityVersion,
                    boundaryIdentity)
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    current,
                    $"The move {boundaryDescription} is temporarily unavailable while its physical generation is being verified."))
            {
                throw new MoveNeedsAttentionException(
                    $"The move {boundaryDescription} changed physical generation.");
            }

            foreach (var segment in SplitMovePathSegments(relativePath, semantics))
            {
                var next = OpenPinnedMoveChild(
                    current,
                    segment,
                    boundaryDescription);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor OpenPinnedMoveChild(
        PinnedDirectoryCreation.PinnedDirectoryAnchor current,
        string segment,
        string authorityDescription)
    {
        try
        {
            return current.OpenExistingChild(segment);
        }
        catch (Exception exception) when (
            IsPinnedMoveHierarchyReplacementError(exception))
        {
            throw new MoveNeedsAttentionException(
                $"A directory beneath the authorized {authorityDescription} changed while its pinned hierarchy was being opened: {exception.Message}");
        }
    }

    private static bool IsPinnedMoveHierarchyReplacementError(Exception exception)
    {
        if (exception is InvalidOperationException)
        {
            return true;
        }

        if (exception is not System.ComponentModel.Win32Exception native)
        {
            return false;
        }

        return OperatingSystem.IsWindows()
            ? native.NativeErrorCode is 2 or 3
            : OperatingSystem.IsLinux()
                ? native.NativeErrorCode is 2 or 20 or 40
                : OperatingSystem.IsMacOS()
                    && native.NativeErrorCode is 2 or 20 or 62;
    }

    private static IEnumerable<string> SplitMovePathSegments(
        string relativePath,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return [];
        }

        var separators = semantics.Syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        return relativePath.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries);
    }

}
