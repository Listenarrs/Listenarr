namespace Listenarr.Application.Search.Matching;

/// <summary>Scoped workflow access to the current immutable annotation snapshot.</summary>
public sealed class ReleaseAnnotationCatalogProvider(IReleaseAnnotationCatalogSource source)
{
    public ReleaseAnnotationCatalog GetSnapshot() => source.GetSnapshot();
}
