namespace Listenarr.Application.Downloads.Import;

public static class ImportQualityEvaluator
{
    public static string Determine(AudioMetadata? metadata, string path)
    {
        if (metadata != null)
        {
            if (!string.IsNullOrEmpty(metadata.Format)) return metadata.Format;
            if (metadata.BitRate.HasValue) return (metadata.BitRate.Value / 1000) + "kbps";
        }

        var name = Path.GetFileName(path) ?? string.Empty;
        if (name.Contains("320", StringComparison.OrdinalIgnoreCase)) return "MP3 320kbps";
        if (name.Contains("256", StringComparison.OrdinalIgnoreCase)) return "MP3 256kbps";
        if (name.Contains("192", StringComparison.OrdinalIgnoreCase)) return "MP3 192kbps";
        if (name.Contains("128", StringComparison.OrdinalIgnoreCase)) return "MP3 128kbps";

        return Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
        {
            "M4B" => "M4B",
            "M4A" => "M4A",
            "MP3" => "MP3",
            "FLAC" => "FLAC",
            "OGG" => "OGG",
            "OPUS" => "OPUS",
            "WMA" => "WMA",
            "AAC" => "AAC",
            "WV" => "WV",
            _ => string.Empty
        };
    }

    public static bool IsAcceptable(string? candidate, string? existing, QualityProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(existing) || profile == null)
        {
            return true;
        }

        return !TryParseBitrate(candidate, out var candidateBitrate)
               || !TryParseBitrate(existing, out var existingBitrate)
               || candidateBitrate >= existingBitrate;
    }

    private static bool TryParseBitrate(string quality, out int bitrate)
    {
        bitrate = 0;
        var match = System.Text.RegularExpressions.Regex.Match(quality, @"\d{2,}");
        return match.Success && int.TryParse(match.Value, out bitrate);
    }
}
