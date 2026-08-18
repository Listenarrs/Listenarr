namespace Listenarr.Domain.Common;

public static partial class FileSystemPathIdentity
{
    public static bool TryCanonicalizeStoredAbsolutePathForHost(
        string path,
        out string canonicalPath,
        out string reason,
        FileSystemPathSyntax? hostSyntax = null)
    {
        canonicalPath = string.Empty;
        reason = string.Empty;
        var effectiveHostSyntax = ResolveHostSyntax(hostSyntax);
        if (!TryDetectAbsoluteSyntax(path, effectiveHostSyntax, out var detectedSyntax))
        {
            reason = TryDetectAbsoluteSyntax(path, out var foreignSyntax)
                ? $"The persisted path uses {foreignSyntax} filesystem syntax, but this host uses {effectiveHostSyntax} syntax."
                : "The persisted path is not absolute and cannot be resolved without changing its identity.";
            return false;
        }

        if (ContainsNavigationSegments(path, detectedSyntax))
        {
            reason = "The persisted path contains a legacy navigation segment and cannot be canonicalized without changing its identity.";
            return false;
        }

        try
        {
            canonicalPath = Canonicalize(path, detectedSyntax);
            return true;
        }
        catch (ArgumentException exception)
        {
            reason = $"The persisted absolute path is invalid: {exception.Message}";
            return false;
        }
    }

    public static bool TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
        string path,
        out string canonicalPath,
        out string reason,
        FileSystemPathSyntax? hostSyntax = null)
    {
        canonicalPath = string.Empty;
        reason = string.Empty;
        var effectiveHostSyntax = ResolveHostSyntax(hostSyntax);
        if (!TryDetectAbsoluteSyntax(path, out var detectedSyntax))
        {
            reason = IsForwardSlashUncPath(path)
                ? "The persisted path does not have an unambiguous absolute filesystem syntax."
                : "The persisted path is not absolute and cannot be resolved without changing its identity.";
            return false;
        }

        if (detectedSyntax != effectiveHostSyntax)
        {
            reason = $"The persisted path uses {detectedSyntax} filesystem syntax, but this host uses {effectiveHostSyntax} syntax.";
            return false;
        }
        if (detectedSyntax == FileSystemPathSyntax.Windows
            && IsWindowsNamespacePath(path))
        {
            reason = "The persisted Windows namespace path cannot be canonicalized as an ordinary filesystem path without changing its identity.";
            return false;
        }

        return TryCanonicalizeStoredAbsolutePathForHost(
            path,
            out canonicalPath,
            out reason,
            effectiveHostSyntax);
    }

    public static bool TryCanonicalizeStoredPathWithIdentityForHost(
        string path,
        PathIdentitySnapshot identity,
        out string canonicalPath,
        out string reason,
        FileSystemPathSyntax? hostSyntax = null)
    {
        canonicalPath = string.Empty;
        reason = string.Empty;
        var effectiveHostSyntax = ResolveHostSyntax(hostSyntax);
        if (identity.Syntax != effectiveHostSyntax)
        {
            reason = $"The persisted identity uses {identity.Syntax} filesystem syntax, but this host uses {effectiveHostSyntax} syntax.";
            return false;
        }

        if (!TryDetectAbsoluteSyntax(path, identity.Syntax, out var detectedSyntax))
        {
            reason = TryDetectAbsoluteSyntax(path, out var foreignSyntax)
                ? $"The persisted path uses {foreignSyntax} filesystem syntax, but its identity uses {identity.Syntax} syntax."
                : "The persisted path is not absolute and cannot be validated against its identity.";
            return false;
        }

        if (ContainsNavigationSegments(path, detectedSyntax))
        {
            reason = "The persisted path contains a legacy navigation segment and cannot be canonicalized without changing its identity.";
            return false;
        }

        if (ContainsNavigationSegments(identity.BoundaryPath, identity.Syntax))
        {
            reason = "The persisted identity boundary contains a legacy navigation segment and cannot be canonicalized safely.";
            return false;
        }

        try
        {
            canonicalPath = Canonicalize(path, identity.Syntax);
            identity.ValidateForPath(canonicalPath);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            reason = exception.Message;
            return false;
        }
    }

    public static bool TryDetectAbsoluteSyntaxForHost(
        string path,
        out FileSystemPathSyntax syntax,
        FileSystemPathSyntax? hostSyntax = null) =>
        TryDetectAbsoluteSyntax(path, ResolveHostSyntax(hostSyntax), out syntax);

    public static bool TryDetectAbsoluteSyntax(
        string path,
        FileSystemPathSyntax expectedSyntax,
        out FileSystemPathSyntax syntax)
    {
        syntax = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (expectedSyntax == FileSystemPathSyntax.Windows)
        {
            if (!WindowsDrivePattern.IsMatch(path)
                && !path.StartsWith("\\\\", StringComparison.Ordinal)
                && !IsForwardSlashUncPath(path))
            {
                return false;
            }

            syntax = FileSystemPathSyntax.Windows;
            return true;
        }

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        syntax = FileSystemPathSyntax.Unix;
        return true;
    }

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

    /// <summary>
    /// Conservatively determines whether a persisted endpoint may overlap a known
    /// boundary. This is a safety predicate only: a same-host or ambiguous spelling
    /// that cannot be proven outside the boundary must be treated as overlapping.
    /// </summary>
    public static bool StoredPathMayTouchBoundary(
        string storedPath,
        string boundaryPath,
        FileSystemPathSemantics boundarySemantics,
        PathIdentitySnapshot? storedIdentity = null,
        FileSystemPathSemantics? storedSemantics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryPath);

        if (boundarySemantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException(
                "Boundary case sensitivity must be resolved before evaluating overlap.");
        }

        if (boundarySemantics.Syntax == FileSystemPathSyntax.Windows
            && IsWindowsNamespacePath(storedPath))
        {
            if (storedIdentity.HasValue)
            {
                try
                {
                    storedIdentity.Value.ValidateForPath(storedPath);
                }
                catch (Exception exception) when (exception is
                    ArgumentException or InvalidOperationException
                        or NotSupportedException or PathTooLongException)
                {
                    return true;
                }
            }

            if ((storedIdentity.HasValue
                    && storedIdentity.Value.Syntax != FileSystemPathSyntax.Windows)
                || (storedSemantics.HasValue
                    && storedSemantics.Value.Syntax != FileSystemPathSyntax.Windows))
            {
                return true;
            }
            if (!TryNormalizeWindowsNamespacePathForSafety(
                    storedPath,
                    out var namespacePath)
                || !TryCanonicalizeStoredAbsolutePathForHost(
                    namespacePath,
                    out var canonicalNamespacePath,
                    out _,
                    FileSystemPathSyntax.Windows))
            {
                return true;
            }

            var namespaceSemantics = storedIdentity?.Semantics
                ?? storedSemantics
                ?? boundarySemantics;
            try
            {
                return EvaluateBoundaryConflict(
                        canonicalNamespacePath,
                        namespaceSemantics,
                        boundaryPath,
                        boundarySemantics)
                    != FileSystemPathBoundaryConflict.None;
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                return true;
            }
        }

        string canonicalStoredPath;
        FileSystemPathSemantics resolvedStoredSemantics;
        if (storedIdentity.HasValue)
        {
            if (storedIdentity.Value.Syntax != boundarySemantics.Syntax)
            {
                return false;
            }

            try
            {
                storedIdentity.Value.ValidateForPath(storedPath);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException)
            {
                return true;
            }

            if (!TryCanonicalizeStoredPathWithIdentityForHost(
                    storedPath,
                    storedIdentity.Value,
                    out canonicalStoredPath,
                    out _,
                    boundarySemantics.Syntax))
            {
                return true;
            }

            resolvedStoredSemantics = storedIdentity.Value.Semantics;
        }
        else
        {
            var hasContextualSyntax = false;
            if (!TryDetectAbsoluteSyntax(storedPath, out var detectedSyntax))
            {
                if (!storedSemantics.HasValue
                    || !TryDetectAbsoluteSyntax(
                        storedPath,
                        storedSemantics.Value.Syntax,
                        out detectedSyntax))
                {
                    // Ambiguous same-host syntax and invalid/relative persisted paths
                    // cannot prove that an active filesystem owner is unrelated.
                    return true;
                }

                hasContextualSyntax = true;
            }
            if (detectedSyntax != boundarySemantics.Syntax)
            {
                return false;
            }

            if (hasContextualSyntax)
            {
                try
                {
                    canonicalStoredPath = Canonicalize(storedPath, detectedSyntax);
                }
                catch (Exception exception) when (exception is
                    ArgumentException or InvalidOperationException
                        or NotSupportedException or PathTooLongException
                        or System.Security.SecurityException)
                {
                    return true;
                }
            }
            else if (!TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    storedPath,
                    out canonicalStoredPath,
                    out _,
                    boundarySemantics.Syntax))
            {
                return true;
            }

            if (storedSemantics.HasValue
                && storedSemantics.Value.Syntax != detectedSyntax)
            {
                return true;
            }

            resolvedStoredSemantics = storedSemantics ?? boundarySemantics;
        }

        try
        {
            return EvaluateBoundaryConflict(
                    canonicalStoredPath,
                    resolvedStoredSemantics,
                    boundaryPath,
                    boundarySemantics)
                != FileSystemPathBoundaryConflict.None;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static bool IsWindowsNamespacePath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || normalized.StartsWith("\\\\.\\", StringComparison.Ordinal);
    }

    private static bool TryNormalizeWindowsNamespacePathForSafety(
        string path,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        var normalized = path.Replace('/', '\\');
        if (!IsWindowsNamespacePath(normalized) || normalized.Length <= 4)
        {
            return false;
        }

        var remainder = normalized[4..];
        if (remainder.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            var uncRemainder = remainder[4..];
            if (string.IsNullOrWhiteSpace(uncRemainder))
            {
                return false;
            }

            normalizedPath = "\\\\" + uncRemainder;
            return true;
        }

        if (!WindowsDrivePattern.IsMatch(remainder))
        {
            return false;
        }

        normalizedPath = remainder;
        return true;
    }

    private static bool ContainsNavigationSegments(
        string path,
        FileSystemPathSyntax syntax)
    {
        var separators = syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        return path.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private static FileSystemPathSyntax ResolveHostSyntax(FileSystemPathSyntax? hostSyntax) =>
        hostSyntax
        ?? (OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix);
}
