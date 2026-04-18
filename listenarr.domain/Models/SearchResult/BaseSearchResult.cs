namespace Listenarr.Domain.Models
{
    /// <summary>
    /// Base class for all search results with common properties
    /// </summary>
    public abstract class BaseSearchResult
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        // Backwards-compatibility: some tests and callers expect `Author` property name.
        public string Author { get => Artist; set => Artist = value; }
        public string Album { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? SourceLink { get; set; } // Direct link to the source
        public string PublishedDate { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public int Score { get; set; }
        public string Uploader { get; set; } = string.Empty;
    }
}