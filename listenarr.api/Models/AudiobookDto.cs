using System;
using System.Collections.Generic;

namespace Listenarr.Api.Models
{
    public class AudiobookFileDto
    {
        public int Id { get; set; }
        public string? Path { get; set; }
        public long? Size { get; set; }
        public double? DurationSeconds { get; set; }
        public string? Format { get; set; }
        public string? Container { get; set; }
        public string? Codec { get; set; }
        public int? Bitrate { get; set; }
        public int? SampleRate { get; set; }
        public int? Channels { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? Source { get; set; }
    }

    public class AudiobookDto
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public string[]? Authors { get; set; }
        public string[]? Narrators { get; set; }
        public string? Asin { get; set; }
        public List<string>? Isbn { get; set; }
        public string? Language { get; set; }
        public List<string>? Genres { get; set; }
        public string[]? Tags { get; set; }
        public string? Description { get; set; }
        public string? PublishYear { get; set; }
        public string? PublishedDate { get; set; }
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public bool? Monitored { get; set; }
        public string? FilePath { get; set; }
        public long? FileSize { get; set; }
        public string? BasePath { get; set; }
        public AudiobookFileDto[]? Files { get; set; }
        public string? ImageUrl { get; set; }
        public string? Quality { get; set; }
        public int? QualityProfileId { get; set; }
        public string? Version { get; set; }
        public bool? Abridged { get; set; }
        public bool? Explicit { get; set; }
        public string? FilePathHash { get; set; }
        public bool? Wanted { get; set; }
    }
}