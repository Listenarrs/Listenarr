using System.Text.Json.Serialization;

namespace Listenarr.Api.Models.Slskd
{
    public class SearchRequestDto
    {
        [JsonPropertyName("searchText")]
        public required string SearchText { get; set; }
        
        [JsonPropertyName("responseLimit")]
        public uint ResponseLimit { get; set; } = 100;
        
        // FIXME: Slskd gives timeout instantaneously when using 15 seconds, maybe their documentation is wrong and value is not in seconds
        //[JsonPropertyName("searchTimeout")]
        //public uint SearchTimeout { get; set; } = 15;
    }
}