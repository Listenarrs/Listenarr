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
        string? bestBoundary = null;
        var bestLength = -1;
        foreach (var root in await rootFolderService.GetAllAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                continue;
            }

            var rootResolution = await semanticsResolver.ResolveAsync(
                canonicalRoot,
                root.CaseSensitivityMode,
                cancellationToken);
            if (rootResolution.State != PathIdentityState.Valid
                || rootResolution.Semantics != destinationResolution.Semantics
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
        RootFolder? bestRoot = null;
        var bestRootLength = -1;
        foreach (var root in await rootFolderService.GetAllAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                continue;
            }

            var resolution = await semanticsResolver.ResolveAsync(
                canonicalRoot,
                root.CaseSensitivityMode,
                cancellationToken);
            if (resolution.State != PathIdentityState.Valid
                || !FileSystemPathIdentity.IsSameOrInside(
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

        return bestRoot?.CaseSensitivityMode ?? FileSystemCaseSensitivityMode.Auto;
    }

    private async Task<FileSystemPathSemantics> ResolvePathSemanticsAsync(
        string path,
        string defaultReason,
        CancellationToken cancellationToken)
    {
        var resolution = await semanticsResolver.ResolveAsync(
            path,
            cancellationToken: cancellationToken);
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
