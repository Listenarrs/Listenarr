using Listenarr.Application.Search.Matching;

namespace Listenarr.Application.Search.Contracts;

/// <summary>Supplies a validated immutable snapshot without exposing resource loading to workflows.</summary>
public interface IReleaseAnnotationCatalogSource
{
    ReleaseAnnotationCatalog GetSnapshot();
}
