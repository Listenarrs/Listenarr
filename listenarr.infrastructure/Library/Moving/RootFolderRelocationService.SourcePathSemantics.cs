using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static StartSourcePathSemantics ResolveStartSourcePathSemantics(
        RootFolder root,
        FileSystemSemanticsResolution? sourceResolution,
        RootFolderRelocationMode mode,
        FileSystemPathSyntax confirmedTargetSyntax)
    {
        var persisted = RootFolderPathSemantics.ResolvePersisted(root);
        var sourceOperationSemantics = persisted?.Semantics
            ?? sourceResolution?.Semantics;
        var storedSourcePathSemantics = persisted
            ?? (sourceResolution == null
                ? null
                : new PersistedRootFolderPathSemantics(
                    sourceResolution.Semantics,
                    DetectAmbiguousCaseMatches: false));
        var metadataSourcePathSemantics = mode == RootFolderRelocationMode.MetadataOnly
            ? RootFolderPathSemantics.ResolveForMetadataRepair(
                    root,
                    confirmedTargetSyntax)
                ?? storedSourcePathSemantics
            : storedSourcePathSemantics;
        var allowContextualAmbiguousMetadataSyntax =
            mode == RootFolderRelocationMode.MetadataOnly
            && storedSourcePathSemantics == null
            && metadataSourcePathSemantics.HasValue;
        var persistedSourceSensitivity = mode == RootFolderRelocationMode.MetadataOnly
            ? metadataSourcePathSemantics?.Semantics.CaseSensitivity
            : sourceOperationSemantics?.CaseSensitivity;
        var sourceCaseSensitivityMode = persistedSourceSensitivity switch
        {
            FileSystemCaseSensitivity.Sensitive => FileSystemCaseSensitivityMode.Sensitive,
            FileSystemCaseSensitivity.Insensitive => FileSystemCaseSensitivityMode.Insensitive,
            _ => root.CaseSensitivityMode
        };

        return new StartSourcePathSemantics(
            sourceOperationSemantics,
            storedSourcePathSemantics,
            metadataSourcePathSemantics,
            allowContextualAmbiguousMetadataSyntax,
            sourceCaseSensitivityMode);
    }

    private sealed record StartSourcePathSemantics(
        FileSystemPathSemantics? SourceOperationSemantics,
        PersistedRootFolderPathSemantics? StoredSourcePathSemantics,
        PersistedRootFolderPathSemantics? MetadataSourcePathSemantics,
        bool AllowContextualAmbiguousMetadataSyntax,
        FileSystemCaseSensitivityMode SourceCaseSensitivityMode);
}
