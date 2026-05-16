using Listenarr.Domain.Models;

namespace Listenarr.Application.Search
{
    /// <summary>
    /// Contains collected ASIN candidates and associated metadata.
    /// </summary>
    public class AsinCandidateCollection
    {
        public List<string> AsinCandidates { get; } = new List<string>();
        public Dictionary<string, (string Title, string Author, string? ImageUrl, string? Language)> AsinToRawResult { get; } = new Dictionary<string, (string, string, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> AsinToSource { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, OpenLibraryBook> AsinToOpenLibrary { get; } = new Dictionary<string, OpenLibraryBook>(StringComparer.OrdinalIgnoreCase);
        public List<SearchResult> OpenLibraryDerivedResults { get; } = new List<SearchResult>();
    }
}
