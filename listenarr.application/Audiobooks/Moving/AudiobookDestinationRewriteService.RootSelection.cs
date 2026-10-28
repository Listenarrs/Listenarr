using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Moving;

public sealed partial class AudiobookDestinationRewriteService
{
    private static bool UnavailableConfiguredRootOutranksTargetBoundary(
        string path,
        MoveRootBoundary targetBoundary,
        IReadOnlyCollection<UnavailableMoveRoot> unavailableRoots)
    {
        if (unavailableRoots.Count == 0)
        {
            return false;
        }
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                path,
                out var pathSyntax))
        {
            return true;
        }

        var targetLength = FileSystemPathIdentity.Canonicalize(
            targetBoundary.Path,
            targetBoundary.Semantics.Syntax).Length;
        foreach (var unavailable in unavailableRoots)
        {
            if (string.IsNullOrWhiteSpace(unavailable.CanonicalPath))
            {
                if (unavailable.Root.Path.Length >= targetLength
                    && FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
                        unavailable.Root.Path,
                        path,
                        pathSyntax,
                        unavailable.Root.CaseSensitivityMode))
                {
                    return true;
                }

                continue;
            }

            if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                    unavailable.CanonicalPath,
                    out var rootSyntax)
                || rootSyntax != pathSyntax
                || unavailable.CanonicalPath.Length < targetLength)
            {
                continue;
            }

            var potentialSemantics = new FileSystemPathSemantics(
                pathSyntax,
                unavailable.Root.CaseSensitivityMode
                    == FileSystemCaseSensitivityMode.Sensitive
                        ? FileSystemCaseSensitivity.Sensitive
                        : FileSystemCaseSensitivity.Insensitive);
            try
            {
                if (FileSystemPathIdentity.IsSameOrInside(
                        path,
                        unavailable.CanonicalPath,
                        potentialSemantics))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                return true;
            }
        }

        return false;
    }
}
