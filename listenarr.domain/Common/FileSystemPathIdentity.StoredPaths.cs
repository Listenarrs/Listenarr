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
