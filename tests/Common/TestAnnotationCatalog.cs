using Listenarr.Application.Search.Matching;
using Listenarr.Infrastructure.Search.Matching;

namespace Listenarr.Tests.Common;

internal static class TestAnnotationCatalog
{
    public static ReleaseAnnotationCatalog Current { get; } = ReleaseAnnotationCatalogLoader.LoadDefault();
}
