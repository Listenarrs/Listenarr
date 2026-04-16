using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

public class ManualImportItemDto
{
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("fullPath")]
    [Required]
    public string? FullPath {
        get;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                field = null;
                return;
            }

            if (value.IndexOfAny(Path.GetInvalidPathChars()) != -1)
            {
                throw new ArgumentException("Path is empty or contains invalid characters.", nameof(value));
            }

            if (value.Contains("..") || value.Contains("./") || value.Contains(".\\"))
            {
                throw new ArgumentException("Path traversal attempts are not allowed.", nameof(value));
            }

            field = Path.GetFullPath(value);
        }
    }

    [JsonPropertyName("matchedAudiobookId")]
    public int MatchedAudiobookId { get; set; }

    [JsonPropertyName("releaseGroup")]
    public string? ReleaseGroup { get; set; }

    [JsonPropertyName("qualityProfileId")]
    public int? QualityProfileId { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }

    [JsonIgnore]
    public int? SequenceNumberHint { get; set; }

    [JsonIgnore]
    public int? DiskNumberHint { get; set; }

    [JsonIgnore]
    public int? ChapterNumberHint { get; set; }
}