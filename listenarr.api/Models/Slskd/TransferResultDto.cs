using System.Text.Json.Serialization;

namespace Listenarr.Api.Models.Slskd
{
    public class TransferResultDto
    {
        [JsonPropertyName("enqueued")]
        public List<TransferResultItemDto> Enqueued { get; set; } = [];
        
        [JsonPropertyName("failed")]

        public List<string> Failed { get; set; } = [];
    }

    public class TransferResultItemDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = string.Empty;

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("startOffset")]
        public long StartOffset { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("stateDescription")]
        public string StateDescription { get; set; } = string.Empty;

        [JsonPropertyName("requestedAt")]
        public DateTime RequestedAt { get; set; }

        [JsonPropertyName("bytesTransferred")]
        public long BytesTransferred { get; set; }

        [JsonPropertyName("averageSpeed")]
        public double AverageSpeed { get; set; }

        [JsonPropertyName("bytesRemaining")]
        public long BytesRemaining { get; set; }

        [JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }
    }
}