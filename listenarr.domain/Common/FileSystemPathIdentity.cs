using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Listenarr.Domain.Common;

public enum FileSystemPathSyntax
{
    Windows,
    Unix
}

public enum FileSystemCaseSensitivity
{
    Unknown,
    Sensitive,
    Insensitive
}

public enum FileSystemCaseSensitivityMode
{
    Auto,
    Sensitive,
    Insensitive
}

public enum PathIdentityState
{
    Valid,
    Conflict,
    Unavailable
}

public readonly record struct FileSystemPathSemantics(
    FileSystemPathSyntax Syntax,
    FileSystemCaseSensitivity CaseSensitivity)
{
    public StringComparer Comparer => CaseSensitivity switch
    {
        FileSystemCaseSensitivity.Sensitive => StringComparer.Ordinal,
        FileSystemCaseSensitivity.Insensitive => StringComparer.OrdinalIgnoreCase,
        _ => throw new InvalidOperationException("Filesystem case sensitivity must be resolved first.")
    };

    public static FileSystemPathSemantics CurrentHostDefault => new(
        OperatingSystem.IsWindows() ? FileSystemPathSyntax.Windows : FileSystemPathSyntax.Unix,
        OperatingSystem.IsWindows()
            ? FileSystemCaseSensitivity.Insensitive
            : FileSystemCaseSensitivity.Sensitive);
}

public readonly record struct PathIdentitySnapshot(
    FileSystemPathSyntax Syntax,
    FileSystemCaseSensitivity CaseSensitivity,
    FileSystemCaseSensitivityMode RequestedMode,
    string BoundaryPath)
{
    public FileSystemPathSemantics Semantics => new(Syntax, CaseSensitivity);

    public void ValidateForPath(string path)
    {
        if (CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException(
                "Filesystem case sensitivity must be resolved before persisting a path identity snapshot.");
        }

        var canonicalBoundary = FileSystemPathIdentity.Canonicalize(BoundaryPath, Syntax);
        var canonicalPath = FileSystemPathIdentity.Canonicalize(path, Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(
                canonicalPath,
                canonicalBoundary,
                Semantics))
        {
            throw new InvalidOperationException(
                "The path identity boundary does not contain the persisted path.");
        }
    }

    public static PathIdentitySnapshot FromResolution(
        FileSystemPathSemantics semantics,
        FileSystemCaseSensitivityMode requestedMode,
        string boundaryPath,
        string path)
    {
        var snapshot = new PathIdentitySnapshot(
            semantics.Syntax,
            semantics.CaseSensitivity,
            requestedMode,
            FileSystemPathIdentity.Canonicalize(boundaryPath, semantics.Syntax));
        snapshot.ValidateForPath(path);
        return snapshot;
    }
}

public static partial class FileSystemPathIdentity
{
    private static readonly Regex WindowsDrivePattern = new(
        "^[A-Za-z]:[\\\\/]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ResolveNativeAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows()
            && (path[0] is '/' or '\\')
            && !(path.Length > 1 && path[1] is '/' or '\\'))
        {
            var currentRoot = Path.GetPathRoot(Environment.CurrentDirectory)
                ?? throw new InvalidOperationException("The current filesystem root is unavailable.");
            return currentRoot + path.TrimStart('/', '\\');
        }

        return Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path);
    }

    public static bool AreEquivalent(
        string left,
        string right,
        FileSystemPathSemantics semantics)
    {
        var comparison = GetComparison(semantics);
        return string.Equals(
            Canonicalize(left, semantics.Syntax),
            Canonicalize(right, semantics.Syntax),
            comparison);
    }

    public static bool AreEquivalentEndpoints(
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity)
    {
        sourceIdentity.ValidateForPath(source);
        targetIdentity.ValidateForPath(target);
        return AreEquivalentEndpoints(
            source,
            sourceIdentity.Semantics,
            target,
            targetIdentity.Semantics);
    }

    public static bool AreEquivalentEndpoints(
        string source,
        FileSystemPathSemantics sourceSemantics,
        string target,
        FileSystemPathSemantics targetSemantics)
    {
        if (sourceSemantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown
            || targetSemantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException(
                "Both endpoint case-sensitivity rules must be resolved before comparison.");
        }
        if (sourceSemantics.Syntax != targetSemantics.Syntax)
        {
            return false;
        }

        var comparisonSensitivity = sourceSemantics.CaseSensitivity == FileSystemCaseSensitivity.Insensitive
            || targetSemantics.CaseSensitivity == FileSystemCaseSensitivity.Insensitive
                ? FileSystemCaseSensitivity.Insensitive
                : FileSystemCaseSensitivity.Sensitive;
        return AreEquivalent(
            source,
            target,
            new FileSystemPathSemantics(sourceSemantics.Syntax, comparisonSensitivity));
    }

    public static FileSystemPathSemantics ResolveComparisonSemantics(
        FileSystemCaseSensitivity existingResolvedSensitivity,
        FileSystemPathSemantics requestedSemantics)
    {
        return existingResolvedSensitivity == FileSystemCaseSensitivity.Unknown
            ? requestedSemantics
            : new FileSystemPathSemantics(requestedSemantics.Syntax, existingResolvedSensitivity);
    }

    public static bool IsSameOrInside(
        string candidate,
        string root,
        FileSystemPathSemantics semantics)
    {
        var normalizedCandidate = Canonicalize(candidate, semantics.Syntax);
        var normalizedRoot = Canonicalize(root, semantics.Syntax);
        var comparison = GetComparison(semantics);
        if (string.Equals(normalizedCandidate, normalizedRoot, comparison))
        {
            return true;
        }

        var separator = semantics.Syntax == FileSystemPathSyntax.Windows ? '\\' : '/';
        var rootBoundary = normalizedRoot.EndsWith(separator)
            ? normalizedRoot
            : normalizedRoot + separator;
        return normalizedCandidate.StartsWith(rootBoundary, comparison);
    }

    public static string CreateKey(
        string scope,
        string path,
        FileSystemPathSemantics semantics,
        int version = 1)
    {
        if (semantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException("Filesystem case sensitivity must be resolved before creating an identity key.");
        }

        var canonical = Canonicalize(path, semantics.Syntax);
        if (semantics.CaseSensitivity == FileSystemCaseSensitivity.Insensitive)
        {
            canonical = canonical.ToUpperInvariant();
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var sensitivity = semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive ? "s" : "i";
        return $"v{version}:{scope}:{sensitivity}:{digest}";
    }

    public static string CreateLookupKey(
        string scope,
        string path,
        FileSystemPathSyntax syntax,
        int version = 1)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var canonical = Canonicalize(path, syntax).ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var syntaxToken = syntax == FileSystemPathSyntax.Windows ? "w" : "u";
        return $"v{version}:{scope}:lookup:{syntaxToken}:{digest}";
    }

    public static bool TryDetectAbsoluteSyntax(
        string path,
        out FileSystemPathSyntax syntax)
    {
        syntax = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (WindowsDrivePattern.IsMatch(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            syntax = FileSystemPathSyntax.Windows;
            return true;
        }

        // A double-forward-slash path is valid Unix syntax and is also commonly
        // used as a Windows UNC spelling. Without an owning filesystem context,
        // choosing either interpretation would be an identity-changing guess.
        if (IsForwardSlashUncPath(path))
        {
            return false;
        }

        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            syntax = FileSystemPathSyntax.Unix;
            return true;
        }

        return false;
    }

    public static bool TryResolveRelativePathWithinBase(
        string basePath,
        string relativePath,
        FileSystemPathSemantics semantics,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrEmpty(relativePath) || IsRooted(relativePath, semantics.Syntax))
        {
            return false;
        }

        var separators = semantics.Syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        if (relativePath.Split(separators).Any(segment => segment is "." or ".."))
        {
            return false;
        }

        var separator = semantics.Syntax == FileSystemPathSyntax.Windows ? '\\' : '/';
        var candidate = Canonicalize(
            Canonicalize(basePath, semantics.Syntax) + separator + relativePath,
            semantics.Syntax);
        if (!IsSameOrInside(candidate, basePath, semantics))
        {
            return false;
        }

        resolvedPath = candidate;
        return true;
    }

    public static string ConvertRelativePathSyntax(
        string relativePath,
        FileSystemPathSyntax sourceSyntax,
        FileSystemPathSyntax targetSyntax)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (sourceSyntax == targetSyntax)
        {
            return relativePath;
        }

        return targetSyntax == FileSystemPathSyntax.Windows
            ? relativePath.Replace('/', '\\')
            : relativePath.Replace('\\', '/');
    }

    public static bool TryGetRelativePathWithinBase(
        string basePath,
        string candidatePath,
        FileSystemPathSemantics semantics,
        out string relativePath)
    {
        relativePath = string.Empty;
        var canonicalBase = Canonicalize(basePath, semantics.Syntax);
        var canonicalCandidate = Canonicalize(candidatePath, semantics.Syntax);
        var comparison = GetComparison(semantics);
        if (string.Equals(canonicalBase, canonicalCandidate, comparison))
        {
            return true;
        }

        var separator = semantics.Syntax == FileSystemPathSyntax.Windows ? '\\' : '/';
        var baseBoundary = canonicalBase.EndsWith(separator)
            ? canonicalBase
            : canonicalBase + separator;
        if (!canonicalCandidate.StartsWith(baseBoundary, comparison))
        {
            return false;
        }

        relativePath = canonicalCandidate[baseBoundary.Length..];
        return true;
    }

    public static string Canonicalize(string path, FileSystemPathSyntax syntax)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        return syntax == FileSystemPathSyntax.Windows
            ? CanonicalizeWindows(path)
            : CanonicalizeUnix(path);
    }

    private static StringComparison GetComparison(FileSystemPathSemantics semantics)
    {
        return semantics.CaseSensitivity switch
        {
            FileSystemCaseSensitivity.Sensitive => StringComparison.Ordinal,
            FileSystemCaseSensitivity.Insensitive => StringComparison.OrdinalIgnoreCase,
            _ => throw new InvalidOperationException(
                "Filesystem case sensitivity must be resolved before comparing paths.")
        };
    }

    private static bool IsRooted(string path, FileSystemPathSyntax syntax)
    {
        return syntax == FileSystemPathSyntax.Windows
            ? WindowsDrivePattern.IsMatch(path)
                || path.StartsWith("\\\\", StringComparison.Ordinal)
                || IsForwardSlashUncPath(path)
            : path.StartsWith("/", StringComparison.Ordinal);
    }

    private static bool IsForwardSlashUncPath(string path)
    {
        if (!path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith("///", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = path[2..].Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2;
    }

    private static string CanonicalizeUnix(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Unix filesystem identity requires an absolute path.", nameof(path));
        }

        var segments = CollapseSegments(path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private static string CanonicalizeWindows(string path)
    {
        var normalized = path.Replace('/', '\\');
        var (root, remaining) = SplitWindowsRoot(normalized);
        var segments = CollapseSegments(remaining.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        return segments.Count == 0
            ? root
            : root.TrimEnd('\\') + "\\" + string.Join('\\', segments);
    }

    private static (string Root, string Remaining) SplitWindowsRoot(string path)
    {
        if (WindowsDrivePattern.IsMatch(path))
        {
            return (char.ToUpperInvariant(path[0]) + ":\\", path[3..]);
        }

        if (!path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Windows filesystem identity requires an absolute path.", nameof(path));
        }

        var position = 2;
        while (position < path.Length && path[position] == '\\')
        {
            position++;
        }

        var serverStart = position;
        while (position < path.Length && path[position] != '\\')
        {
            position++;
        }
        var server = path[serverStart..position];

        while (position < path.Length && path[position] == '\\')
        {
            position++;
        }

        var shareStart = position;
        while (position < path.Length && path[position] != '\\')
        {
            position++;
        }
        var share = path[shareStart..position];
        if (string.IsNullOrWhiteSpace(server)
            || string.IsNullOrWhiteSpace(share)
            || server is "." or ".."
            || share is "." or "..")
        {
            throw new ArgumentException("UNC paths require a valid server and share.", nameof(path));
        }

        return ($"\\\\{server}\\{share}", path[position..]);
    }

    private static List<string> CollapseSegments(IEnumerable<string> source)
    {
        var segments = new List<string>();
        foreach (var segment in source)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return segments;
    }
}
