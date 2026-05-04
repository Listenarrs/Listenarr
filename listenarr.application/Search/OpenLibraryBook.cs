using System.Text.Json.Serialization;

namespace Listenarr.Application.Search
{
    public class OpenLibraryBook
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("author_name")]
        public List<string>? AuthorName { get; set; }

        [JsonPropertyName("author_key")]
        public List<string>? AuthorKey { get; set; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("isbn")]
        public List<string> Isbn { get; set; } = new();

        [JsonPropertyName("publisher")]
        public List<string>? Publisher { get; set; }

        [JsonPropertyName("cover_i")]
        public int? CoverId { get; set; }

        [JsonPropertyName("edition_count")]
        public int? EditionCount { get; set; }

        [JsonPropertyName("language")]
        public List<string>? Language { get; set; }

        [JsonPropertyName("subject")]
        public List<string>? Subject { get; set; }
    }
}
