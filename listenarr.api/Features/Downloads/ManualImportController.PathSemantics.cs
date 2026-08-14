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

    private Task<FileSystemSemanticsResolution> ResolveDestinationResolutionAsync(
        string? basePath,
        IReadOnlyCollection<RootFolder> rootFolders,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            throw new InvalidOperationException("Destination base path is unavailable.");
        }

        return ResolvePathResolutionAsync(
            basePath,
            rootFolders,
            "Destination filesystem identity is unavailable.",
            cancellationToken);
    }

    private async Task<FileSystemPathSemantics> ResolvePathSemanticsAsync(
        string path,
        IReadOnlyCollection<RootFolder> rootFolders,
        string defaultReason,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolvePathResolutionAsync(
            path,
            rootFolders,
            defaultReason,
            cancellationToken);
        return resolution.Semantics;
    }

    private async Task<FileSystemSemanticsResolution> ResolvePathResolutionAsync(
        string path,
        IReadOnlyCollection<RootFolder> rootFolders,
        string defaultReason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rootFolders);

        FileSystemSemanticsResolution? bestRootResolution = null;
        var bestRootLength = -1;
        foreach (var root in rootFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                continue;
            }

            var rootResolution = await ResolveConfiguredRootSemanticsAsync(
                root,
                canonicalRoot,
                cancellationToken);
            if (rootResolution.State != PathIdentityState.Valid
                || !FileSystemPathIdentity.IsSameOrInside(
                    path,
                    canonicalRoot,
                    rootResolution.Semantics))
            {
                continue;
            }

            if (canonicalRoot.Length > bestRootLength)
            {
                bestRootResolution = rootResolution;
                bestRootLength = canonicalRoot.Length;
            }
        }

        FileSystemSemanticsResolution resolution;
        if (bestRootResolution != null)
        {
            var resolvedMode = bestRootResolution.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivityMode.Sensitive
                    : FileSystemCaseSensitivityMode.Insensitive;
            resolution = await _semanticsResolver.ResolveAsync(
                path,
                resolvedMode,
                cancellationToken);
        }
        else
        {
            resolution = await _semanticsResolver.ResolveAsync(
                path,
                FileSystemCaseSensitivityMode.Auto,
                cancellationToken);
        }

        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(resolution.Reason ?? defaultReason);
        }

        return resolution;
    }

    private async Task<FileSystemSemanticsResolution> ResolveConfiguredRootSemanticsAsync(
        RootFolder root,
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        var persisted = RootFolderPathSemantics.ResolvePersisted(root);
        if (persisted.HasValue
            && !persisted.Value.DetectAmbiguousCaseMatches)
        {
            return new FileSystemSemanticsResolution(
                persisted.Value.Semantics,
                PathIdentityState.Valid,
                canonicalRoot,
                CanonicalPath: canonicalRoot);
        }

        return await _semanticsResolver.ResolveAsync(
            canonicalRoot,
            root.CaseSensitivityMode,
            cancellationToken);
    }

    private static bool IsPotentiallyInsideAnyConfiguredRoot(
        string path,
        IEnumerable<RootFolder> rootFolders)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out _)
            || !FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                canonicalPath,
                out var pathSyntax))
        {
            return true;
        }

        foreach (var root in rootFolders)
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _)
                || !FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                    canonicalRoot,
                    out var rootSyntax)
                || rootSyntax != pathSyntax)
            {
                continue;
            }

            var sensitive = new FileSystemPathSemantics(
                pathSyntax,
                FileSystemCaseSensitivity.Sensitive);
            var insensitive = new FileSystemPathSemantics(
                pathSyntax,
                FileSystemCaseSensitivity.Insensitive);
            if (FileSystemPathIdentity.IsSameOrInside(
                    canonicalPath,
                    canonicalRoot,
                    sensitive)
                || FileSystemPathIdentity.IsSameOrInside(
                    canonicalPath,
                    canonicalRoot,
                    insensitive))
            {
                return true;
            }
        }

        return false;
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

            var resolution = await ResolveConfiguredRootSemanticsAsync(
                rootFolder,
                canonicalRoot,
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
