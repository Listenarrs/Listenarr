using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Library;

public partial class RootFoldersController
{
    private async Task<FileSystemPathSemantics> ResolveFolderSemanticsAsync(
        RootFolder folder)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                folder.Path,
                out var canonicalPath,
                out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var resolution = await _semanticsResolver.ResolveAsync(
            canonicalPath,
            folder.CaseSensitivityMode);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                resolution.Reason
                    ?? "Root folder filesystem identity could not be resolved.");
        }

        return resolution.Semantics;
    }

    private static string? TryCanonicalizePathForComparison(
        string? path,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return FileSystemPathIdentity.Canonicalize(
                path,
                semantics.Syntax);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
