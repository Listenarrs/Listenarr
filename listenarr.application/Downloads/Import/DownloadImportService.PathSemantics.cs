using Listenarr.Domain.Common;

namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private async Task<FileSystemSemanticsResolution> ResolveDestinationResolutionAsync(
        string basePath,
        CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                basePath,
                out var canonicalBasePath,
                out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var mode = await ResolveDestinationCaseSensitivityModeAsync(
            canonicalBasePath,
            cancellationToken);
        var resolution = await semanticsResolver.ResolveAsync(
            canonicalBasePath,
            mode,
            cancellationToken);
        return resolution.State == PathIdentityState.Valid
            ? resolution
            : throw new InvalidOperationException(
                resolution.Reason ?? "Destination filesystem identity is unavailable.");
    }

    private async Task<string> ResolveDestinationOwnershipBoundaryAsync(
        string basePath,
        FileSystemSemanticsResolution destinationResolution,
        CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                basePath,
                out var basePathSyntax))
        {
            throw new InvalidOperationException(
                "The destination ownership path does not have a valid host filesystem identity.");
        }

        string? bestBoundary = null;
        var bestLength = -1;
        var unavailableRootLength = -1;
        foreach (var root in await rootFolderService.GetAllAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                if (FileSystemPathIdentity.StoredBoundaryMayContainPath(
                        root.Path,
                        basePath,
                        basePathSyntax,
                        root.CaseSensitivityMode))
                {
                    unavailableRootLength = Math.Max(
                        unavailableRootLength,
                        root.Path.Length);
                }

                continue;
            }
            if (!FileSystemPathIdentity.StoredBoundaryMayContainPath(
                    canonicalRoot,
                    basePath,
                    basePathSyntax,
                    root.CaseSensitivityMode))
            {
                continue;
            }

            var rootResolution = await semanticsResolver.ResolveAsync(
                canonicalRoot,
                root.CaseSensitivityMode,
                cancellationToken);
            if (rootResolution.State != PathIdentityState.Valid)
            {
                unavailableRootLength = Math.Max(
                    unavailableRootLength,
                    canonicalRoot.Length);
                continue;
            }
            if (rootResolution.Semantics != destinationResolution.Semantics
                || !FileSystemPathIdentity.IsSameOrInside(
                    basePath,
                    canonicalRoot,
                    rootResolution.Semantics))
            {
                continue;
            }

            var canonicalBoundaryRoot = FileSystemPathIdentity.Canonicalize(
                string.IsNullOrWhiteSpace(rootResolution.CanonicalPath)
                    ? canonicalRoot
                    : rootResolution.CanonicalPath,
                rootResolution.Semantics.Syntax);
            if (canonicalBoundaryRoot.Length > bestLength)
            {
                bestBoundary = canonicalBoundaryRoot;
                bestLength = canonicalBoundaryRoot.Length;
            }
        }

        if (unavailableRootLength >= bestLength
            && unavailableRootLength >= 0)
        {
            throw new InvalidOperationException(
                "A configured root that may contain this download-import destination has unavailable or ambiguous persisted filesystem identity. Repair or change that root before importing here.");
        }

        var boundary = bestBoundary ?? destinationResolution.BoundaryPath;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidOperationException(
                "The destination ownership boundary is unavailable.");
        }

        return FileSystemPathIdentity.Canonicalize(
            boundary,
            destinationResolution.Semantics.Syntax);
    }

    private async Task<FileSystemCaseSensitivityMode> ResolveDestinationCaseSensitivityModeAsync(
        string basePath,
        CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                basePath,
                out var basePathSyntax))
        {
            throw new InvalidOperationException(
                "The download-import destination does not have a valid host filesystem identity.");
        }

        RootFolder? bestRoot = null;
        var bestRootLength = -1;
        var unavailableRootLength = -1;
        foreach (var root in await rootFolderService.GetAllAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                if (FileSystemPathIdentity.StoredBoundaryMayContainPath(
                        root.Path,
                        basePath,
                        basePathSyntax,
                        root.CaseSensitivityMode))
                {
                    unavailableRootLength = Math.Max(
                        unavailableRootLength,
                        root.Path.Length);
                }

                continue;
            }
            if (!FileSystemPathIdentity.StoredBoundaryMayContainPath(
                    canonicalRoot,
                    basePath,
                    basePathSyntax,
                    root.CaseSensitivityMode))
            {
                continue;
            }

            var resolution = await semanticsResolver.ResolveAsync(
                canonicalRoot,
                root.CaseSensitivityMode,
                cancellationToken);
            if (resolution.State != PathIdentityState.Valid)
            {
                unavailableRootLength = Math.Max(
                    unavailableRootLength,
                    canonicalRoot.Length);
                continue;
            }
            if (!FileSystemPathIdentity.IsSameOrInside(
                    basePath,
                    canonicalRoot,
                    resolution.Semantics))
            {
                continue;
            }

            if (canonicalRoot.Length > bestRootLength)
            {
                bestRoot = root;
                bestRootLength = canonicalRoot.Length;
            }
        }

        if (unavailableRootLength >= bestRootLength
            && unavailableRootLength >= 0)
        {
            throw new InvalidOperationException(
                "A configured root that may contain this download-import destination has unavailable or ambiguous persisted filesystem identity. Repair or change that root before importing here.");
        }

        return bestRoot?.CaseSensitivityMode ?? FileSystemCaseSensitivityMode.Auto;
    }

    private async Task<FileSystemPathSemantics> ResolvePathSemanticsAsync(
        string path,
        string defaultReason,
        CancellationToken cancellationToken)
    {
        var resolution = await semanticsResolver.ResolveAsync(
            path,
            FileSystemCaseSensitivityMode.Auto,
            cancellationToken);
        return resolution.State == PathIdentityState.Valid
            ? resolution.Semantics
            : throw new InvalidOperationException(resolution.Reason ?? defaultReason);
    }

    private static string NormalizeAuthoritativeBasePath(
        string basePath,
        FileSystemSemanticsResolution resolution)
    {
        return string.IsNullOrWhiteSpace(resolution.CanonicalPath)
            ? FileSystemPathIdentity.Canonicalize(basePath, resolution.Semantics.Syntax)
            : FileSystemPathIdentity.Canonicalize(
                resolution.CanonicalPath,
                resolution.Semantics.Syntax);
    }
}
