using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private bool TryResolveManagedDestinationBasePath(
        Audiobook audiobook,
        IReadOnlyCollection<RootFolder> rootFolders,
        ApplicationSettings settings,
        out string managedBasePath,
        out IReadOnlyList<string> allowedRoots,
        out string reason)
    {
        managedBasePath = string.Empty;
        reason = string.Empty;
        allowedRoots = FileUtils.GetValidMutationRootsForCurrentOs(
            rootFolders
                .Select(root => root.Path)
                .Append(settings.OutputPath));
        if (allowedRoots.Count == 0)
        {
            reason = "No configured destination root is available.";
            return false;
        }

        var requestedBasePath = !string.IsNullOrWhiteSpace(audiobook.BasePath)
            ? audiobook.BasePath
            : !string.IsNullOrWhiteSpace(settings.OutputPath)
                ? settings.OutputPath
                : rootFolders.FirstOrDefault(root => root.IsDefault)?.Path
                    ?? rootFolders.FirstOrDefault()?.Path;
        if (string.IsNullOrWhiteSpace(requestedBasePath)
            || !_fileSystem.TryValidateMutationTarget(
                requestedBasePath,
                allowedRoots,
                out managedBasePath,
                out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason)
                ? "The audiobook destination is outside configured roots."
                : reason;
            return false;
        }

        return true;
    }

    private async Task<FileSystemSemanticsResolution> ResolveDestinationResolutionAsync(
        string? basePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            throw new InvalidOperationException("Destination base path is unavailable.");
        }

        RootFolder? bestRoot = null;
        var bestRootLength = -1;
        foreach (var root in await _rootFolderService.GetAllAsync())
        {
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
                || !FileSystemPathIdentity.IsSameOrInside(
                    basePath,
                    canonicalRoot,
                    rootResolution.Semantics))
            {
                continue;
            }

            if (canonicalRoot.Length > bestRootLength)
            {
                bestRoot = root;
                bestRootLength = canonicalRoot.Length;
            }
        }

        var resolution = await _semanticsResolver.ResolveAsync(
            basePath,
            bestRoot?.CaseSensitivityMode ?? FileSystemCaseSensitivityMode.Auto,
            cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                resolution.Reason ?? "Destination filesystem identity is unavailable.");
        }

        return resolution;
    }

    private async Task<FileSystemPathSemantics> ResolvePathSemanticsAsync(
        string path,
        string defaultReason,
        CancellationToken cancellationToken)
    {
        var resolution = await _semanticsResolver.ResolveAsync(
            path,
            cancellationToken: cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(resolution.Reason ?? defaultReason);
        }

        return resolution.Semantics;
    }

    private async Task<bool> IsInsideAnyConfiguredRootAsync(
        string path,
        IEnumerable<RootFolder> rootFolders,
        CancellationToken cancellationToken)
    {
        foreach (var rootFolder in rootFolders)
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    rootFolder.Path,
                    out var canonicalRoot,
                    out _))
            {
                continue;
            }

            var resolution = await _semanticsResolver.ResolveAsync(
                canonicalRoot,
                rootFolder.CaseSensitivityMode,
                cancellationToken);
            if (resolution.State == PathIdentityState.Valid
                && FileSystemPathIdentity.IsSameOrInside(
                    path,
                    canonicalRoot,
                    resolution.Semantics))
            {
                return true;
            }
        }

        return false;
    }
}
