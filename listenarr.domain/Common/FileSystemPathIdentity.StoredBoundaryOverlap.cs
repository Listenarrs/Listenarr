namespace Listenarr.Domain.Common;

public static partial class FileSystemPathIdentity
{
    /// <summary>
    /// Conservatively determines whether a persisted boundary may contain a known
    /// host path under the boundary's requested case-sensitivity mode. This is a
    /// safety predicate only: contextual interpretation must never be used to
    /// authorize, normalize, or persist an ambiguous or otherwise invalid boundary.
    /// </summary>
    public static bool StoredBoundaryMayContainPath(
        string storedBoundary,
        string candidatePath,
        FileSystemPathSyntax candidateSyntax,
        FileSystemCaseSensitivityMode boundaryCaseSensitivityMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedBoundary);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        if (StoredBoundaryMayContainPathCore(
                storedBoundary, candidatePath, candidateSyntax, boundaryCaseSensitivityMode))
        {
            return true;
        }

        // Padding can hide a malformed configured boundary from an outer-root
        // fallback. Trimming widens this safety fence only; it never supplies a
        // canonical path or grants authority to the original persisted boundary.
        var trimmedBoundary = storedBoundary.Trim();
        return trimmedBoundary.Length != storedBoundary.Length
            && trimmedBoundary.Length != 0
            && StoredBoundaryMayContainPathCore(
                trimmedBoundary, candidatePath, candidateSyntax, boundaryCaseSensitivityMode);
    }

    private static bool StoredBoundaryMayContainPathCore(
        string storedBoundary,
        string candidatePath,
        FileSystemPathSyntax candidateSyntax,
        FileSystemCaseSensitivityMode boundaryCaseSensitivityMode)
    {
        if (candidateSyntax == FileSystemPathSyntax.Windows
            && IsWindowsNamespacePath(storedBoundary))
        {
            if (!TryNormalizeWindowsNamespacePathForSafety(
                    storedBoundary,
                    out var namespaceBoundary))
            {
                return true;
            }

            storedBoundary = namespaceBoundary;
        }

        FileSystemPathSyntax boundarySyntax;
        if (TryDetectAbsoluteSyntax(storedBoundary, out var unambiguousSyntax))
        {
            if (unambiguousSyntax != candidateSyntax)
            {
                return false;
            }
            boundarySyntax = unambiguousSyntax;
            if (ContainsNavigationSegments(storedBoundary, boundarySyntax))
            {
                return true;
            }
        }
        else
        {
            if (!TryDetectAbsoluteSyntax(
                    storedBoundary,
                    candidateSyntax,
                    out var contextualSyntax)
                || contextualSyntax != candidateSyntax)
            {
                return false;
            }
            boundarySyntax = contextualSyntax;
            if (ContainsNavigationSegments(storedBoundary, boundarySyntax))
            {
                return true;
            }
        }

        try
        {
            var contextualBoundary = Canonicalize(
                storedBoundary,
                boundarySyntax);
            var sensitivity = boundaryCaseSensitivityMode
                == FileSystemCaseSensitivityMode.Sensitive
                    ? FileSystemCaseSensitivity.Sensitive
                    : FileSystemCaseSensitivity.Insensitive;
            return IsSameOrInside(
                candidatePath,
                contextualBoundary,
                new FileSystemPathSemantics(
                    candidateSyntax,
                    sensitivity));
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            // Failure to compare a syntax-compatible boundary must not become
            // permission to borrow unrelated live semantics.
            return true;
        }
    }

    public static bool AmbiguousStoredBoundaryMayContainPath(
        string storedBoundary,
        string candidatePath,
        FileSystemPathSyntax candidateSyntax,
        FileSystemCaseSensitivityMode boundaryCaseSensitivityMode) =>
        StoredBoundaryMayContainPath(
            storedBoundary,
            candidatePath,
            candidateSyntax,
            boundaryCaseSensitivityMode);
}
