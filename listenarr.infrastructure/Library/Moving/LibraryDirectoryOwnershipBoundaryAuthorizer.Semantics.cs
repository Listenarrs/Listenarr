using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class LibraryDirectoryOwnershipBoundaryAuthorizer
{
    private async Task<bool> ConfiguredRootSemanticsCurrentAsync(
        RootFolder root,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        try
        {
            var live = await _semanticsResolver.ResolveAsync(
                root.Path,
                root.CaseSensitivityMode,
                cancellationToken);
            return live.State == PathIdentityState.Valid
                && live.Semantics.Syntax == semantics.Syntax
                && live.Semantics.CaseSensitivity == semantics.CaseSensitivity;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or NotSupportedException or PathTooLongException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static FileSystemPathSemantics? TryGetPersistedRootSemantics(
        RootFolder root)
    {
        var persisted = RootFolderPathSemantics.ResolvePersisted(root);
        return persisted.HasValue
            && !persisted.Value.DetectAmbiguousCaseMatches
            ? persisted.Value.Semantics
            : null;
    }

    private static bool HasCompatibleSyntax(
        string path,
        FileSystemPathSyntax expectedSyntax) =>
        FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            path,
            out var syntax)
        && syntax == expectedSyntax;
}
