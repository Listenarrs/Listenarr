using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    internal static StringComparer DurableRecoveryArtifactNameComparer { get; } =
        StringComparer.Ordinal;

    // Durable recovery evidence records a specific logical pathname. Preserve path
    // segment casing even on Windows because directory namespaces can be
    // case-sensitive. Any alias acceptance must be proved separately by pinned
    // physical identity rather than inferred from the host OS.
    private static string CanonicalizeDurablePathEvidence(string path)
    {
        var syntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix;
        return FileSystemPathIdentity.Canonicalize(
            Path.GetFullPath(path),
            syntax);
    }

    internal static bool DurablePathEvidenceEquals(
        string first,
        string second,
        FileSystemPathSyntax? syntax = null)
    {
        var effectiveSyntax = syntax
            ?? (OperatingSystem.IsWindows()
                ? FileSystemPathSyntax.Windows
                : FileSystemPathSyntax.Unix);
        var normalizedFirst = syntax.HasValue
            ? FileSystemPathIdentity.Canonicalize(first, effectiveSyntax)
            : CanonicalizeDurablePathEvidence(first);
        var normalizedSecond = syntax.HasValue
            ? FileSystemPathIdentity.Canonicalize(second, effectiveSyntax)
            : CanonicalizeDurablePathEvidence(second);
        return string.Equals(
            normalizedFirst,
            normalizedSecond,
            StringComparison.Ordinal);
    }
}
