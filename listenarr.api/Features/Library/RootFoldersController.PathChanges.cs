using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Library;

public partial class RootFoldersController
{
    private static bool HasRootPathChanged(
        RootFolder existing,
        string normalizedRequestedPath)
    {
        var persistedSourceSemantics =
            RootFolderPathSemantics.ResolvePersisted(existing)?.Semantics;
        if (!persistedSourceSemantics.HasValue)
        {
            return true;
        }

        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                normalizedRequestedPath,
                out var requestedSyntax)
            || requestedSyntax != persistedSourceSemantics.Value.Syntax)
        {
            return true;
        }

        return !FileSystemPathIdentity.AreEquivalent(
            existing.Path,
            normalizedRequestedPath,
            persistedSourceSemantics.Value);
    }
}
