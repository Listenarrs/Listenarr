using Listenarr.Application.Metadata;

namespace Listenarr.Application.Audiobooks
{
    public sealed class AuthorCatalogFetchResult
    {
        public AuthorLookupItem Author { get; set; } = new();

        public List<AudibleSearchResult> Books { get; set; } = new();

        public int TotalBooks => Books.Count;
    }
}
