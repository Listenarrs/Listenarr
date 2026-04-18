using System.Text.Json.Serialization;

public class SlskdDownloadIdDto
{
    [JsonPropertyName("ids")]
    public string[] Ids { get; set; } = [];
}