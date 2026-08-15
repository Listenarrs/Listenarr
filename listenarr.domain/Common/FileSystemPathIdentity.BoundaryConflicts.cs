namespace Listenarr.Domain.Common;

public enum FileSystemPathBoundaryConflict
{
    None,
    Equivalent,
    FirstInsideSecond,
    SecondInsideFirst,
    Ambiguous
}

public static partial class FileSystemPathIdentity
{
    public static FileSystemPathBoundaryConflict EvaluateBoundaryConflict(
        string firstPath,
        FileSystemPathSemantics firstSemantics,
        string secondPath,
        FileSystemPathSemantics secondSemantics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondPath);
        EnsureResolved(firstSemantics);
        EnsureResolved(secondSemantics);

        if (firstSemantics.Syntax != secondSemantics.Syntax)
        {
            return FileSystemPathBoundaryConflict.None;
        }

        if (AreEquivalent(firstPath, secondPath, firstSemantics)
            || AreEquivalent(firstPath, secondPath, secondSemantics))
        {
            return FileSystemPathBoundaryConflict.Equivalent;
        }

        var firstInsideSecond = IsSameOrInside(
                firstPath,
                secondPath,
                firstSemantics)
            || IsSameOrInside(
                firstPath,
                secondPath,
                secondSemantics);
        var secondInsideFirst = IsSameOrInside(
                secondPath,
                firstPath,
                firstSemantics)
            || IsSameOrInside(
                secondPath,
                firstPath,
                secondSemantics);

        return (firstInsideSecond, secondInsideFirst) switch
        {
            (true, false) => FileSystemPathBoundaryConflict.FirstInsideSecond,
            (false, true) => FileSystemPathBoundaryConflict.SecondInsideFirst,
            (true, true) => FileSystemPathBoundaryConflict.Ambiguous,
            _ => FileSystemPathBoundaryConflict.None
        };
    }

    private static void EnsureResolved(FileSystemPathSemantics semantics)
    {
        if (semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException(
                "Filesystem case sensitivity must be resolved before evaluating path-boundary conflicts.");
        }
    }
}
