namespace Listenarr.Application.Metadata.Audnexus
{
    /// <summary>
    /// Response from Audnexus book endpoint
    /// </summary>
    public class AudnexusBookResponse
    {
        public string? Asin { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public List<AudnexusAuthor>? Authors { get; set; }
        public List<AudnexusNarrator>? Narrators { get; set; }
        public string? PublisherName { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Description { get; set; }
        public string? Summary { get; set; }
        public string? Image { get; set; }
        public int? RuntimeLengthMin { get; set; }
        public string? Language { get; set; }
        public string? FormatType { get; set; }
        public List<AudnexusGenre>? Genres { get; set; }
        public AudnexusSeries? SeriesPrimary { get; set; }
        public AudnexusSeries? SeriesSecondary { get; set; }
        public string? Rating { get; set; }
        public int? Copyright { get; set; }
        public string? Isbn { get; set; }
        public string? Region { get; set; }
        public bool? IsAdult { get; set; }
        public string? LiteratureType { get; set; }
    }

    public class AudnexusAuthor
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
    }

    public class AudnexusNarrator
    {
        public string? Name { get; set; }
    }

    public class AudnexusGenre
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
    }

    public class AudnexusSeries
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Position { get; set; }
    }

    /// <summary>
    /// Response from Audnexus author search endpoint
    /// </summary>
    public class AudnexusAuthorSearchResult
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Image { get; set; }
        public string? Region { get; set; }
        public List<AudnexusGenre>? Genres { get; set; }
        public List<AudnexusSimilarAuthor>? Similar { get; set; }
    }

    public class AudnexusSimilarAuthor
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>
    /// Response from Audnexus author endpoint
    /// </summary>
    public class AudnexusAuthorResponse
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Image { get; set; }
        public string? Region { get; set; }
        public List<AudnexusGenre>? Genres { get; set; }
        public List<AudnexusSimilarAuthor>? Similar { get; set; }
    }

    /// <summary>
    /// Response from Audnexus chapters endpoint
    /// </summary>
    public class AudnexusChapterResponse
    {
        public string? Asin { get; set; }
        public string? Region { get; set; }
        public int? BrandIntroDurationMs { get; set; }
        public int? BrandOutroDurationMs { get; set; }
        public bool? IsAccurate { get; set; }
        public int? RuntimeLengthMs { get; set; }
        public int? RuntimeLengthSec { get; set; }
        public List<AudnexusChapter>? Chapters { get; set; }
    }

    public class AudnexusChapter
    {
        public int? LengthMs { get; set; }
        public int? StartOffsetMs { get; set; }
        public int? StartOffsetSec { get; set; }
        public string? Title { get; set; }
    }
}
