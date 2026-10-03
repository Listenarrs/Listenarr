namespace Listenarr.Application.Metadata.Audible
{
    public class AudibleBookResponse
    {
        public string? Asin { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public List<AudibleAuthor>? Authors { get; set; }
        public List<AudibleNarrator>? Narrators { get; set; }
        public string? Publisher { get; set; }
        public string? PublishDate { get; set; }
        public string? Description { get; set; }
        public string? ImageUrl { get; set; }
        public int? LengthMinutes { get; set; }
        public string? Language { get; set; }
        public List<AudibleGenre>? Genres { get; set; }
        public List<AudibleSeries>? Series { get; set; }
        public bool? Explicit { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Isbn { get; set; }
        public string? Region { get; set; }
        public string? BookFormat { get; set; }
        public string? ContentType { get; set; }
        public string? ContentDeliveryType { get; set; }
        public string? EpisodeType { get; set; }
        public string? Sku { get; set; }
    }

    public class AudibleAuthor { public string? Asin { get; set; } public string? Name { get; set; } public string? Region { get; set; } }
    public class AudibleNarrator { public string? Name { get; set; } }
    public class AudibleGenre { public string? Asin { get; set; } public string? Name { get; set; } public string? Type { get; set; } }
    public class AudibleSeries { public string? Asin { get; set; } public string? Name { get; set; } public string? Position { get; set; } }

    public class AudibleSearchResponse
    {
        public List<AudibleSearchResult>? Results { get; set; }
        public int? TotalResults { get; set; }

        /// <summary>
        /// True when Audible did not answer, so an empty <see cref="Results"/> means the
        /// lookup failed rather than the catalogue having nothing. Callers that treat an
        /// empty list as "this book does not exist" need to check this first.
        /// </summary>
        public bool ProviderUnavailable { get; set; }
    }

    public class AudibleSearchResult
    {
        public string? Asin { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public List<AudibleAuthor>? Authors { get; set; }
        public string? ImageUrl { get; set; }
        // Runtime fields: audible may return different names (runtimeLengthMin, lengthMinutes, runtimeMinutes)
        public int? RuntimeLengthMin { get; set; }
        public int? LengthMinutes { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string? Language { get; set; }
        public string? ContentType { get; set; }
        public string? ContentDeliveryType { get; set; }
        public string? EpisodeType { get; set; }
        public string? Sku { get; set; }
        public string? BookFormat { get; set; }
        public List<AudibleGenre>? Genres { get; set; }
        public List<AudibleSeries>? Series { get; set; }
        public string? Publisher { get; set; }
        public List<AudibleNarrator>? Narrators { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Link { get; set; }
        public string? Isbn { get; set; }
    }

    // Helper types for simple author lookup parsing
    public class AuthorLookupItem { public string? Asin { get; set; } public string? Name { get; set; } public string? Image { get; set; } public string? Region { get; set; } public string? Description { get; set; } }
    public class AuthorLookupEnvelope
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Image { get; set; }
        public string? Region { get; set; }
        public string? Description { get; set; }
        public List<AuthorLookupItem>? Results { get; set; }
    }
    public class SeriesLookupItem
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Region { get; set; }
        public string? Description { get; set; }
        public string? Position { get; set; }
        public string? Image { get; set; }
    }
    public class SeriesLookupEnvelope
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Region { get; set; }
        public string? Description { get; set; }
        public string? Position { get; set; }
        public List<SeriesLookupItem>? Results { get; set; }
    }
    public class AudibleAuthorTileMetadata { public List<AudibleAuthorTileAuthor>? Authors { get; set; } }
    public class AudibleAuthorTileAuthor { public string? Name { get; set; } }
}
