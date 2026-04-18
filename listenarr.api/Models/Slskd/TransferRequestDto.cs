using System.Text.Json.Serialization;

namespace Listenarr.Api.Models.Slskd
{
    public class TransferRequestItemDto
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;
        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}