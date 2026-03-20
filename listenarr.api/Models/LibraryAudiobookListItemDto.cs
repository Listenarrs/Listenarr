using System;

namespace Listenarr.Api.Models
{
    public class LibraryAudiobookListItemDto
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string[]? Authors { get; set; }
        public string[]? Narrators { get; set; }
        public string? PublishYear { get; set; }
        public string? PublishedDate { get; set; }
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string[]? Genres { get; set; }
        public string? Asin { get; set; }
        public string? OpenLibraryId { get; set; }
        public string? Publisher { get; set; }
        public string? Language { get; set; }
        public int? Runtime { get; set; }
        public string? ImageUrl { get; set; }
        public bool Monitored { get; set; }
        public string? BasePath { get; set; }
        // Transitional legacy primary file summary retained for filters and upgrade compatibility.
        public string? FilePath { get; set; }
        public long? FileSize { get; set; }
        public int FileCount { get; set; }
        public string? Quality { get; set; }
        public int? QualityProfileId { get; set; }
        public string[]? AuthorAsins { get; set; }
        public bool Wanted { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
