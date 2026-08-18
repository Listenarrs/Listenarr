namespace Listenarr.Domain.Common;

public static partial class FileSystemPathIdentity
{
    /// <summary>
    /// Conservatively compares a persisted path with a known candidate for exact
    /// path identity. Recognized Windows namespace spellings are normalized only
    /// for conflict detection; they are never returned for persistence or authority.
    /// </summary>
    public static bool StoredPathMayIdentifySamePath(
        string storedPath,
        string candidatePath,
        FileSystemPathSemantics candidateSemantics,
        FileSystemPathSemantics? storedSemantics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        if (candidateSemantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException(
                "Candidate case sensitivity must be resolved before comparing stored paths.");
        }

        var comparisonPath = storedPath;
        if (candidateSemantics.Syntax == FileSystemPathSyntax.Windows
            && IsWindowsNamespacePath(storedPath))
        {
            if (!TryNormalizeWindowsNamespacePathForSafety(
                    storedPath,
                    out comparisonPath))
            {
                // An unrecognized Windows namespace path can still alias an ordinary
                // path through volume or device mappings, so exact separation is not proven.
                return true;
            }
        }
        else
        {
            if (!TryDetectAbsoluteSyntax(storedPath, out var detectedSyntax))
            {
                if (!TryDetectAbsoluteSyntax(
                        storedPath,
                        candidateSemantics.Syntax,
                        out detectedSyntax))
                {
                    return false;
                }
            }
            if (detectedSyntax != candidateSemantics.Syntax)
            {
                return false;
            }
        }

        if (!TryCanonicalizeStoredAbsolutePathForHost(
                comparisonPath,
                out var canonicalStoredPath,
                out _,
                candidateSemantics.Syntax))
        {
            return false;
        }

        var effectiveStoredSemantics = storedSemantics ?? candidateSemantics;
        if (effectiveStoredSemantics.Syntax != candidateSemantics.Syntax
            || effectiveStoredSemantics.CaseSensitivity
                == FileSystemCaseSensitivity.Unknown)
        {
            return false;
        }

        try
        {
            return AreEquivalentEndpoints(
                canonicalStoredPath,
                effectiveStoredSemantics,
                candidatePath,
                candidateSemantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            return true;
        }
    }
}
