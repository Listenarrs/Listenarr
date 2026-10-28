using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Files;

public sealed class AudiobookFilePathIdentityResolver(
    IRootFolderRepository rootFolderRepository,
    IFileSystemSemanticsResolver semanticsResolver) : IAudiobookFilePathIdentityResolver
{
    private readonly object _rootFoldersSync = new();
    private Task<List<RootFolder>>? _rootFoldersTask;

    public async ValueTask<AudiobookFilePathIdentity> ResolveAsync(
        Audiobook audiobook,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var absolutePath = ResolveAbsolutePath(audiobook, path, out var syntax);
        var canonicalPath = FileSystemPathIdentity.Canonicalize(absolutePath, syntax);
        var hostSyntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix;
        if (syntax != hostSyntax)
        {
            return AudiobookFilePathIdentity.CreateUnavailable(
                canonicalPath,
                syntax,
                FileSystemCaseSensitivityMode.Auto,
                canonicalPath,
                $"The persisted path uses {syntax} filesystem syntax, which cannot be validated on the current {hostSyntax} host.");
        }

        var rootSearch = await FindAuthoritativeRootAsync(
            canonicalPath,
            syntax,
            cancellationToken);
        if (rootSearch.UnavailableRoot != null)
        {
            return AudiobookFilePathIdentity.CreateUnavailable(
                canonicalPath,
                syntax,
                rootSearch.UnavailableRoot.CaseSensitivityMode,
                canonicalPath,
                rootSearch.UnavailableReason
                    ?? "A configured root that may contain this audiobook file has unavailable persisted filesystem identity.");
        }

        var rootMatch = rootSearch.Match;
        var requestedMode = rootMatch?.Root.CaseSensitivityMode
            ?? FileSystemCaseSensitivityMode.Auto;
        var resolution = rootMatch?.Resolution
            ?? await semanticsResolver.ResolveAsync(
                absolutePath,
                requestedMode,
                cancellationToken);
        var boundaryPath = CanonicalizeBoundary(
            resolution.BoundaryPath,
            canonicalPath,
            resolution.Semantics);

        if (resolution.State != PathIdentityState.Valid
            || resolution.Semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            return AudiobookFilePathIdentity.CreateUnavailable(
                canonicalPath,
                syntax,
                requestedMode,
                boundaryPath,
                resolution.Reason ?? "Filesystem identity is unavailable.");
        }

        if (resolution.Semantics.Syntax != syntax)
        {
            return AudiobookFilePathIdentity.CreateUnavailable(
                canonicalPath,
                syntax,
                requestedMode,
                boundaryPath,
                "Resolved filesystem syntax does not match the audiobook file path syntax.");
        }

        var snapshot = PathIdentitySnapshot.FromResolution(
            resolution.Semantics,
            requestedMode,
            boundaryPath,
            canonicalPath);
        return AudiobookFilePathIdentity.CreateValid(
            canonicalPath,
            snapshot.Semantics,
            snapshot.RequestedMode,
            snapshot.BoundaryPath);
    }

    private async Task<RootSearchResult> FindAuthoritativeRootAsync(
        string canonicalPath,
        FileSystemPathSyntax syntax,
        CancellationToken cancellationToken)
    {
        var roots = await GetRootFoldersAsync();
        RootMatch? best = null;
        UnavailableRootMatch? deepestUnavailable = null;
        foreach (var root in roots.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                if (FileSystemPathIdentity.StoredBoundaryMayContainPath(
                        root.Path,
                        canonicalPath,
                        syntax,
                        root.CaseSensitivityMode)
                    && (deepestUnavailable == null
                        || root.Path.Length > deepestUnavailable.CanonicalRootLength))
                {
                    deepestUnavailable = new UnavailableRootMatch(
                        root,
                        root.Path.Length,
                        "A configured root that may contain this audiobook file has ambiguous persisted filesystem identity. Repair or change that root before persisting file path identity here.");
                }

                continue;
            }
            if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    canonicalRoot,
                    out var rootSyntax)
                || rootSyntax != syntax)
            {
                continue;
            }

            if (!FileSystemPathIdentity.StoredBoundaryMayContainPath(
                    canonicalRoot,
                    canonicalPath,
                    syntax,
                    root.CaseSensitivityMode))
            {
                continue;
            }

            FileSystemSemanticsResolution resolution;
            if (root.PathIdentityState == PathIdentityState.Valid
                && root.ResolvedCaseSensitivity != FileSystemCaseSensitivity.Unknown)
            {
                resolution = new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(rootSyntax, root.ResolvedCaseSensitivity),
                    PathIdentityState.Valid,
                    canonicalRoot);
            }
            else
            {
                resolution = await semanticsResolver.ResolveAsync(
                    canonicalRoot,
                    root.CaseSensitivityMode,
                    cancellationToken);
            }

            if (resolution.State != PathIdentityState.Valid
                || resolution.Semantics.Syntax != syntax)
            {
                if (deepestUnavailable == null
                    || canonicalRoot.Length > deepestUnavailable.CanonicalRootLength)
                {
                    deepestUnavailable = new UnavailableRootMatch(
                        root,
                        canonicalRoot.Length,
                        resolution.Reason
                            ?? "A configured root that may contain this audiobook file has unavailable filesystem semantics.");
                }
                continue;
            }
            if (!FileSystemPathIdentity.IsSameOrInside(
                    canonicalPath,
                    canonicalRoot,
                    resolution.Semantics))
            {
                continue;
            }

            if (best == null || canonicalRoot.Length > best.CanonicalRootLength)
            {
                best = new RootMatch(root, resolution, canonicalRoot.Length);
            }
        }

        if (deepestUnavailable != null
            && deepestUnavailable.CanonicalRootLength
                >= (best?.CanonicalRootLength ?? -1))
        {
            return new RootSearchResult(
                Match: null,
                deepestUnavailable.Root,
                deepestUnavailable.Reason);
        }

        return new RootSearchResult(best, UnavailableRoot: null, UnavailableReason: null);
    }

    private Task<List<RootFolder>> GetRootFoldersAsync()
    {
        lock (_rootFoldersSync)
        {
            return _rootFoldersTask ??= rootFolderRepository.GetAllAsync();
        }
    }

    private static string ResolveAbsolutePath(
        Audiobook audiobook,
        string path,
        out FileSystemPathSyntax syntax)
    {
        FileSystemPathSyntax? baseSyntax = null;
        if (!string.IsNullOrWhiteSpace(audiobook.BasePath)
            && FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                audiobook.BasePath,
                out var detectedBaseSyntax))
        {
            baseSyntax = detectedBaseSyntax;
        }

        if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(path, out syntax))
        {
            return FileSystemPathIdentity.Canonicalize(path, syntax);
        }

        if (!baseSyntax.HasValue)
        {
            throw new InvalidOperationException(
                "A relative audiobook file path requires an authoritative absolute audiobook base path.");
        }

        syntax = baseSyntax.Value;
        var containmentSemantics = new FileSystemPathSemantics(
            syntax,
            FileSystemCaseSensitivity.Sensitive);
        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                audiobook.BasePath!,
                path,
                containmentSemantics,
                out var resolvedPath))
        {
            throw new InvalidOperationException(
                "The relative audiobook file path could not be resolved safely within the audiobook base path.");
        }

        return resolvedPath;
    }

    private static string CanonicalizeBoundary(
        string boundaryPath,
        string canonicalPath,
        FileSystemPathSemantics semantics)
    {
        try
        {
            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    boundaryPath,
                    semantics.Syntax,
                    out _))
            {
                var canonicalBoundary = FileSystemPathIdentity.Canonicalize(
                    boundaryPath,
                    semantics.Syntax);
                var containmentSemantics = semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown
                    ? new FileSystemPathSemantics(
                        semantics.Syntax,
                        FileSystemCaseSensitivity.Sensitive)
                    : semantics;
                if (FileSystemPathIdentity.IsSameOrInside(
                        canonicalPath,
                        canonicalBoundary,
                        containmentSemantics))
                {
                    return canonicalBoundary;
                }
            }
        }
        catch (ArgumentException)
        {
        }

        return canonicalPath;
    }

    private sealed record RootSearchResult(
        RootMatch? Match,
        RootFolder? UnavailableRoot,
        string? UnavailableReason);

    private sealed record UnavailableRootMatch(
        RootFolder Root,
        int CanonicalRootLength,
        string Reason);

    private sealed record RootMatch(
        RootFolder Root,
        FileSystemSemanticsResolution Resolution,
        int CanonicalRootLength);
}
