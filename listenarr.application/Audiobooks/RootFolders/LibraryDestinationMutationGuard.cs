using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.RootFolders;

public sealed class LibraryDestinationMutationGuard(
    IRootFolderService rootFolderService,
    IRootFolderRelocationService relocationService,
    IFileSystemSemanticsResolver semanticsResolver,
    IAudiobookRepository audiobookRepository) : ILibraryDestinationMutationGuard
{
    public async Task<string?> GetBlockingReasonAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var semantics = await ResolveDestinationSemanticsAsync(
            destinationPath,
            cancellationToken);
        if (!semantics.HasValue)
        {
            return "Destination filesystem identity is unavailable.";
        }

        if (await relocationService.IsBoundaryProtectedAsync(
                destinationPath,
                semantics.Value,
                cancellationToken))
        {
            return "Destination overlaps an active root folder relocation.";
        }

        var existingPaths = await audiobookRepository.GetOtherPathReferenceSnapshotsAsync(
            audiobookId: 0,
            cancellationToken);
        foreach (var existing in existingPaths)
        {
            if (string.IsNullOrWhiteSpace(existing.BasePath))
            {
                continue;
            }

            if (FileSystemPathIdentity.StoredPathMayIdentifySamePath(
                    existing.BasePath,
                    destinationPath,
                    semantics.Value))
            {
                return "Destination is already assigned to another audiobook in the library.";
            }
        }

        return null;
    }

    private async Task<FileSystemPathSemantics?> ResolveDestinationSemanticsAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                destinationPath,
                out var destinationSyntax))
        {
            return null;
        }

        var roots = await rootFolderService.GetAllAsync();
        FileSystemPathSemantics? bestSemantics = null;
        var bestRootLength = -1;
        var unavailableRootLength = -1;
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root.Path)))
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                if (FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
                        root.Path,
                        destinationPath,
                        destinationSyntax,
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
                    destinationPath,
                    destinationSyntax,
                    root.CaseSensitivityMode))
            {
                continue;
            }

            try
            {
                var resolution = await semanticsResolver.ResolveAsync(
                    canonicalRoot,
                    root.CaseSensitivityMode,
                    cancellationToken);
                var persisted = RootFolderPathSemantics.ResolvePersisted(root);
                if (resolution.State != PathIdentityState.Valid
                    || !persisted.HasValue
                    || persisted.Value.DetectAmbiguousCaseMatches
                    || persisted.Value.Semantics.Syntax != resolution.Semantics.Syntax
                    || persisted.Value.Semantics.CaseSensitivity
                        != resolution.Semantics.CaseSensitivity)
                {
                    unavailableRootLength = Math.Max(
                        unavailableRootLength,
                        canonicalRoot.Length);
                    continue;
                }
                if (!FileSystemPathIdentity.IsSameOrInside(
                        destinationPath,
                        canonicalRoot,
                        resolution.Semantics))
                {
                    continue;
                }

                if (canonicalRoot.Length > bestRootLength)
                {
                    bestSemantics = resolution.Semantics;
                    bestRootLength = canonicalRoot.Length;
                }
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException
                    or StackOverflowException))
            {
                unavailableRootLength = Math.Max(
                    unavailableRootLength,
                    canonicalRoot.Length);
            }
        }

        if (unavailableRootLength >= bestRootLength
            && unavailableRootLength >= 0)
        {
            return null;
        }
        if (bestSemantics.HasValue)
        {
            return bestSemantics.Value;
        }

        var directResolution = await semanticsResolver.ResolveAsync(
            destinationPath,
            FileSystemCaseSensitivityMode.Auto,
            cancellationToken);
        return directResolution.State == PathIdentityState.Valid
            ? directResolution.Semantics
            : null;
    }
}
