namespace Listenarr.Api.Services
{
    public interface IAuthorCatalogService
    {
        Task<AuthorCatalogFetchResult?> GetCatalogAsync(
            string name,
            string region = "us",
            int limit = 250,
            string? language = null,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default);
    }

    public sealed class AuthorCatalogFetchResult
    {
        public AuthorLookupItem? Author { get; set; }

        public List<AudibleSearchResult> Books { get; set; } = new();

        public int TotalBooks => Books.Count;
    }
}
