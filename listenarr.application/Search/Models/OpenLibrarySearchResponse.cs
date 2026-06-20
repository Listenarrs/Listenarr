using System.Text.Json.Serialization;

namespace Listenarr.Application.Search.Models
{
    public class OpenLibrarySearchResponse
    {
        [JsonPropertyName("start")]
        public int Start { get; set; }

        [JsonPropertyName("num_found")]
        public int NumFound { get; set; }

        [JsonPropertyName("docs")]
        public List<OpenLibraryBook> Docs { get; set; } = new();
    }
}
