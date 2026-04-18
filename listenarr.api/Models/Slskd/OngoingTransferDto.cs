using System.Text.Json.Serialization;

public class OngoingTransferDto
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("directories")]
    public List<OngoingTransferDirectoryDto> Directories { get; set; } = [];
}

public class OngoingTransferDirectoryDto
{
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = string.Empty;

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("files")]
    public List<OngoingTransferFileDto> Files { get; set; } = [];
}

public class OngoingTransferFileDto
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

    [JsonPropertyName("enqueuedAt")]
    public DateTime EnqueuedAt { get; set; }

    [JsonPropertyName("bytesTransferred")]
    public long BytesTransferred { get; set; }

    [JsonPropertyName("averageSpeed")]
    public double AverageSpeed { get; set; }

    [JsonPropertyName("bytesRemaining")]
    public long BytesRemaining { get; set; }

    [JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }
}