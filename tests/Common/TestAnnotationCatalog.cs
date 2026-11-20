using Listenarr.Application.Search.Matching;
using Listenarr.Infrastructure.Search.Matching;

namespace Listenarr.Tests.Common;

internal static class TestAnnotationCatalog
{
    public static ReleaseAnnotationCatalog Current { get; } = ReleaseAnnotationCatalogLoader.LoadDefault();

    public static ReleaseAnnotationCatalogProvider CreateProvider(ReleaseAnnotationCatalog? snapshot = null) =>
        new(CreateSource(snapshot));

    public static IReleaseAnnotationCatalogSource CreateSource(ReleaseAnnotationCatalog? snapshot = null) =>
        new FixedSource(snapshot ?? Current);

    private sealed class FixedSource(ReleaseAnnotationCatalog snapshot) : IReleaseAnnotationCatalogSource
    {
        public ReleaseAnnotationCatalog GetSnapshot() => snapshot;
    }
}
