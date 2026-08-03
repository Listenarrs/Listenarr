using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.RootFolders;

public sealed class LibraryDestinationMutationGuard(
    IRootFolderService rootFolderService,
    IRootFolderRelocationService relocationService,
    IFileSystemSemanticsResolver semanticsResolver) : ILibraryDestinationMutationGuard
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

        return await relocationService.IsBoundaryProtectedAsync(
            destinationPath,
            semantics.Value,
            cancellationToken)
            ? "Destination overlaps an active root folder relocation."
            : null;
    }

    private async Task<FileSystemPathSemantics?> ResolveDestinationSemanticsAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var roots = await rootFolderService.GetAllAsync();
        foreach (var root in roots
            .Where(root => !string.IsNullOrWhiteSpace(root.Path))
            .OrderByDescending(root => root.Path.Length))
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _))
            {
                continue;
            }

            try
            {
                var resolution = await semanticsResolver.ResolveAsync(
                    canonicalRoot,
                    root.CaseSensitivityMode,
                    cancellationToken);
                if (resolution.State == PathIdentityState.Valid
                    && FileSystemPathIdentity.IsSameOrInside(
                        destinationPath,
                        canonicalRoot,
                        resolution.Semantics))
                {
                    return resolution.Semantics;
                }
            }
            catch (ArgumentException)
            {
                // Invalid legacy roots are ignored while resolving the destination's
                // authoritative configured filesystem identity.
            }
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
