using Listenarr.Domain.Common;

namespace Listenarr.Domain.Audiobooks;

public readonly record struct PersistedRootFolderPathSemantics(
    FileSystemPathSemantics Semantics,
    bool DetectAmbiguousCaseMatches);

public static class RootFolderPathSemantics
{
    public static PersistedRootFolderPathSemantics? ResolvePersisted(RootFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                root.Path,
                out var syntax))
        {
            return null;
        }

        return CreatePersistedSemantics(root, syntax);
    }

    public static PersistedRootFolderPathSemantics? ResolveForMetadataRepair(
        RootFolder root,
        FileSystemPathSyntax confirmedSyntax)
    {
        ArgumentNullException.ThrowIfNull(root);
        var persisted = ResolvePersisted(root);
        if (persisted.HasValue)
        {
            return persisted;
        }

        if (!root.Path.StartsWith("//", StringComparison.Ordinal)
            || !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                root.Path,
                confirmedSyntax,
                out var detectedSyntax)
            || detectedSyntax != confirmedSyntax)
        {
            return null;
        }

        // This interpretation is only for explicit metadata-only repair. The
        // persisted spelling remains unavailable for filesystem authorization.
        var sensitivity = root.CaseSensitivityMode switch
        {
            FileSystemCaseSensitivityMode.Sensitive => FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Insensitive => FileSystemCaseSensitivity.Insensitive,
            _ => FileSystemCaseSensitivity.Sensitive
        };
        return new PersistedRootFolderPathSemantics(
            new FileSystemPathSemantics(confirmedSyntax, sensitivity),
            DetectAmbiguousCaseMatches:
                root.CaseSensitivityMode == FileSystemCaseSensitivityMode.Auto);
    }

    private static PersistedRootFolderPathSemantics CreatePersistedSemantics(
        RootFolder root,
        FileSystemPathSyntax syntax)
    {
        var sensitivity = root.CaseSensitivityMode switch
        {
            FileSystemCaseSensitivityMode.Sensitive => FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Insensitive => FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Auto
                when root.PathIdentityState == PathIdentityState.Valid
                    && root.ResolvedCaseSensitivity != FileSystemCaseSensitivity.Unknown =>
                root.ResolvedCaseSensitivity,
            _ => FileSystemCaseSensitivity.Sensitive
        };
        var detectAmbiguousCaseMatches =
            root.CaseSensitivityMode == FileSystemCaseSensitivityMode.Auto
            && (root.PathIdentityState != PathIdentityState.Valid
                || root.ResolvedCaseSensitivity == FileSystemCaseSensitivity.Unknown);

        return new PersistedRootFolderPathSemantics(
            new FileSystemPathSemantics(syntax, sensitivity),
            detectAmbiguousCaseMatches);
    }
}
