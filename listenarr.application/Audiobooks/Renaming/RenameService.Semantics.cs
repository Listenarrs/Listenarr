using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private async Task<RenamePathResolution> ResolveRenamePathResolutionAsync(
        string path,
        List<RootFolder> rootFolders,
        CancellationToken cancellationToken)
    {
        var boundaryFromPersistedRoot = string.IsNullOrWhiteSpace(path);
        var boundaryPath = !boundaryFromPersistedRoot
            ? path
            : rootFolders.FirstOrDefault(root => root.IsDefault)?.Path
                ?? rootFolders.FirstOrDefault(root => !string.IsNullOrWhiteSpace(root.Path))?.Path;
        if (string.IsNullOrWhiteSpace(boundaryPath))
        {
            throw new InvalidOperationException(
                "Filesystem semantics are required for organize operations.");
        }

        if (boundaryFromPersistedRoot
            && !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                boundaryPath,
                out boundaryPath,
                out var boundaryReason))
        {
            throw new InvalidOperationException(boundaryReason);
        }

        RenamePathResolution? bestMatch = null;
        foreach (var root in rootFolders.Where(root => !string.IsNullOrWhiteSpace(root.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                continue;
            }

            var rootResolution = await _semanticsResolver.ResolveAsync(
                canonicalRoot,
                root.CaseSensitivityMode,
                cancellationToken);
            if (rootResolution.State != PathIdentityState.Valid
                || rootResolution.Semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown
                || !FileSystemPathIdentity.IsSameOrInside(
                    boundaryPath,
                    canonicalRoot,
                    rootResolution.Semantics))
            {
                continue;
            }

            var candidate = new RenamePathResolution(
                rootResolution.Semantics,
                root.CaseSensitivityMode,
                canonicalRoot);
            if (bestMatch == null
                || canonicalRoot.Length > bestMatch.BoundaryPath.Length)
            {
                bestMatch = candidate;
            }
        }

        if (bestMatch != null)
        {
            return bestMatch;
        }

        var resolution = await _semanticsResolver.ResolveAsync(
            boundaryPath,
            cancellationToken: cancellationToken);
        if (resolution.State != PathIdentityState.Valid
            || resolution.Semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException(
                resolution.Reason
                ?? "Filesystem semantics are required for organize operations.");
        }

        var identityBoundary = string.IsNullOrWhiteSpace(resolution.BoundaryPath)
            ? boundaryPath
            : resolution.BoundaryPath;
        return new RenamePathResolution(
            resolution.Semantics,
            FileSystemCaseSensitivityMode.Auto,
            FileSystemPathIdentity.Canonicalize(
                identityBoundary,
                resolution.Semantics.Syntax));
    }

    private static RenamePathSemanticsSnapshot ToSnapshot(
        RenamePathResolution resolution) =>
        new()
        {
            Syntax = resolution.Semantics.Syntax,
            CaseSensitivity = resolution.Semantics.CaseSensitivity,
            RequestedMode = resolution.RequestedMode,
            BoundaryPath = resolution.BoundaryPath
        };

    private static bool SemanticsMatch(
        RenamePathSemanticsSnapshot expected,
        RenamePathResolution current)
    {
        if (expected.Syntax != current.Semantics.Syntax
            || expected.CaseSensitivity != current.Semantics.CaseSensitivity
            || expected.RequestedMode != current.RequestedMode
            || string.IsNullOrWhiteSpace(expected.BoundaryPath))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                expected.BoundaryPath,
                current.BoundaryPath,
                current.Semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private sealed record RenamePathResolution(
        FileSystemPathSemantics Semantics,
        FileSystemCaseSensitivityMode RequestedMode,
        string BoundaryPath);
}
