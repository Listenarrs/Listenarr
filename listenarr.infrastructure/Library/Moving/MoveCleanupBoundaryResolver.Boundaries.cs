using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class MoveCleanupBoundaryResolver
{
    private static MoveCleanupBoundaryResolution SelectNarrowerBoundary(
        string persistedBoundary,
        string configuredBoundary,
        FileSystemPathSemantics semantics)
    {
        try
        {
            if (FileSystemPathIdentity.AreEquivalent(
                    persistedBoundary,
                    configuredBoundary,
                    semantics))
            {
                return new MoveCleanupBoundaryResolution(
                    configuredBoundary,
                    MoveCleanupBoundaryKind.ConfiguredRoot);
            }

            if (FileSystemPathIdentity.IsSameOrInside(
                    persistedBoundary,
                    configuredBoundary,
                    semantics))
            {
                return new MoveCleanupBoundaryResolution(
                    persistedBoundary,
                    MoveCleanupBoundaryKind.Persisted);
            }

            if (FileSystemPathIdentity.IsSameOrInside(
                    configuredBoundary,
                    persistedBoundary,
                    semantics))
            {
                return new MoveCleanupBoundaryResolution(
                    configuredBoundary,
                    MoveCleanupBoundaryKind.ConfiguredRoot);
            }

            return Unavailable(
                "The persisted and configured source cleanup boundaries do not describe the same source ancestry.");
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            return Unavailable(
                $"The source cleanup boundaries could not be compared safely: {exception.Message}");
        }
    }

    private static string? FindDeepestCommonAncestor(
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        if (sourceSemantics.Syntax != targetSemantics.Syntax)
        {
            return null;
        }

        var candidate = source;
        while (!string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                if (FileSystemPathIdentity.IsSameOrInside(
                        source,
                        candidate,
                        sourceSemantics)
                    && FileSystemPathIdentity.IsSameOrInside(
                        target,
                        candidate,
                        targetSemantics))
                {
                    return IsFilesystemRoot(candidate, sourceSemantics)
                        ? null
                        : candidate;
                }
            }
            catch (ArgumentException)
            {
                return null;
            }

            candidate = Path.GetDirectoryName(candidate);
        }

        return null;
    }

    private static string? FindSourceVolumeAnchor(
        string source,
        string sourceParent,
        FileSystemPathSemantics semantics)
    {
        var volumeRoot = ResolveVolumeRoot(source, semantics);
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            return null;
        }

        if (semantics.Syntax == FileSystemPathSyntax.Unix
            && FileSystemPathIdentity.AreEquivalent(volumeRoot, "/", semantics))
        {
            // The host root is too broad to infer a user-owned library boundary safely.
            return null;
        }

        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(volumeRoot, source);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var firstSegment = relativePath
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment => segment is not "." and not "..");
        if (string.IsNullOrWhiteSpace(firstSegment)
            || Path.IsPathRooted(firstSegment))
        {
            return null;
        }

        var anchor = Path.Combine(volumeRoot, firstSegment);
        return FileSystemPathIdentity.IsSameOrInside(sourceParent, anchor, semantics)
            && !FileSystemPathIdentity.AreEquivalent(anchor, volumeRoot, semantics)
                ? anchor
                : null;
    }

    private static string? ResolveVolumeRoot(
        string source,
        FileSystemPathSemantics semantics)
    {
        if (semantics.Syntax == FileSystemPathSyntax.Windows)
        {
            return Path.GetPathRoot(source);
        }

        try
        {
            return new DriveInfo(source).RootDirectory.FullName;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsFilesystemRoot(
        string path,
        FileSystemPathSemantics semantics)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrWhiteSpace(root)
            && FileSystemPathIdentity.AreEquivalent(fullPath, root, semantics);
    }

    private static MoveCleanupBoundaryResolution Unavailable(string reason) =>
        new(null, MoveCleanupBoundaryKind.Unavailable, reason);

    private sealed record ConfiguredRootCandidate(
        int CanonicalLength,
        string? Boundary,
        FileSystemPathSemantics? Semantics,
        string? UnavailableReason);

    private readonly record struct ConfiguredBoundaryResolution(
        string? Boundary,
        FileSystemPathSemantics? Semantics,
        string? UnavailableReason);
}
