using Listenarr.Application.Search.Matching;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Search.Matching;

/// <summary>Loads and retains the embedded policy once per application service container.</summary>
public sealed class EmbeddedReleaseAnnotationCatalogSource : IReleaseAnnotationCatalogSource
{
    private readonly ReleaseAnnotationCatalog _snapshot;

    public EmbeddedReleaseAnnotationCatalogSource(ILogger<EmbeddedReleaseAnnotationCatalogSource> logger)
    {
        _snapshot = ReleaseAnnotationCatalogLoader.LoadDefault();
        if (!_snapshot.IsAvailable)
            logger.LogError("Release annotation catalog is unavailable; automatic identity confirmation is deferred");
    }

    public ReleaseAnnotationCatalog GetSnapshot() => _snapshot;
}
