using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving
{
    public sealed partial class AudiobookFilesystemDeleteService
    {
        private async Task<FileSystemPathSemantics?> ResolveDeleteSemanticsAsync(
            string? boundaryPath,
            AudiobookFilesystemDeleteResult result,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(boundaryPath))
            {
                return null;
            }

            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    boundaryPath,
                    out var canonicalBoundaryPath,
                    out _))
            {
                result.Warnings.Add(
                    "The audiobook filesystem path is unavailable on the current host, so deletion was blocked.");
                return null;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                        canonicalBoundaryPath,
                        out var boundarySyntax))
                {
                    result.Warnings.Add(
                        "The audiobook filesystem path syntax is unavailable on the current host, so deletion was blocked.");
                    return null;
                }

                FileSystemPathSemantics? bestSemantics = null;
                var bestRootLength = -1;
                var unavailableRootLength = -1;
                foreach (var root in await _rootFolderService.GetAllAsync())
                {
                    if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                            root.Path,
                            out var canonicalRoot,
                            out _))
                    {
                        if (FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
                                root.Path,
                                canonicalBoundaryPath,
                                boundarySyntax,
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
                            canonicalBoundaryPath,
                            boundarySyntax,
                            root.CaseSensitivityMode))
                    {
                        continue;
                    }

                    var rootResolution = await _semanticsResolver.ResolveAsync(
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
                    if (!FileSystemPathIdentity.IsSameOrInside(
                            canonicalBoundaryPath,
                            canonicalRoot,
                            rootResolution.Semantics))
                    {
                        continue;
                    }

                    if (canonicalRoot.Length > bestRootLength)
                    {
                        bestSemantics = rootResolution.Semantics;
                        bestRootLength = canonicalRoot.Length;
                    }
                }

                if (unavailableRootLength >= bestRootLength
                    && unavailableRootLength >= 0)
                {
                    result.Warnings.Add(
                        "A configured root that may contain this audiobook has ambiguous persisted filesystem identity, so deletion was blocked.");
                    return null;
                }

                if (bestSemantics.HasValue)
                {
                    return bestSemantics.Value;
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(exception, "Failed to resolve root folder semantics while deleting audiobook files");
                result.Warnings.Add(
                    "Configured root filesystem semantics could not be resolved safely, so deletion was blocked.");
                return null;
            }

            var resolution = await _semanticsResolver.ResolveAsync(
                canonicalBoundaryPath,
                FileSystemCaseSensitivityMode.Auto,
                cancellationToken);
            if (resolution.State == PathIdentityState.Valid)
            {
                return resolution.Semantics;
            }

            result.Warnings.Add(
                "Filesystem case sensitivity could not be resolved, so deletion was blocked.");
            return null;
        }
    }
}
