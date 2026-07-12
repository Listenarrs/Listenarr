using Listenarr.Domain.Common;

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

    /// <summary>Project an already-imported library file onto the shared quality vocabulary.</summary>
    public static AudioQualityInput FromFile(AudiobookFile file) => new()
    {
        Codec = file.Codec,
        Container = file.Container,
        Format = file.Format,
        BitrateBitsPerSecond = file.Bitrate,
        Path = file.Path
    };

    /// <summary>Project an incoming download's extracted metadata onto the shared quality vocabulary.</summary>
    public static AudioQualityInput FromMetadata(AudioMetadata? metadata, string path) => new()
    {
        Codec = metadata?.Codec,
        Container = metadata?.Container,
        Format = metadata?.Format,
        BitrateBitsPerSecond = metadata?.BitRate,
        Path = path
    };

    /// <summary>Human-readable quality label for import logs: the matched profile rung when the
    /// profile can rank the file, otherwise the file's own format/extension.</summary>
    public static string Describe(AudioQualityInput input, QualityProfile? profile)
    {
        var rung = QualityMatcher.MatchLabel(input, profile);
        if (!string.IsNullOrWhiteSpace(rung)) return rung!;
        if (!string.IsNullOrWhiteSpace(input.Format)) return input.Format!;
        return Determine(null, input.Path ?? string.Empty);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <b>not worse</b> than <paramref name="existing"/> and may
    /// therefore be imported. Equal quality is acceptable on purpose: a multi-file audiobook imports
    /// parts that all share the same quality as the parts already on disk, and requiring a strict
    /// upgrade would skip every one of them. (Automatic search wants the stricter
    /// <see cref="QualityMatcher.IsLabelBetter"/> instead.)
    /// </summary>
    public static bool IsAcceptable(AudioQualityInput candidate, AudioQualityInput existing, QualityProfile? profile)
    {
        if (profile?.Qualities is { Count: > 0 })
        {
            var candidateMatch = QualityMatcher.Match(candidate, profile);
            var existingMatch = QualityMatcher.Match(existing, profile);

            // Both sides land on a profile rung, so the profile's own ordering decides.
            if (candidateMatch.IsMatch && existingMatch.IsMatch)
            {
                return candidateMatch.Rung!.Priority <= existingMatch.Rung!.Priority;
            }
        }

        // No profile, an empty profile, or a side the profile cannot rank. Fall back to the codec and
        // bitrate facts so gating still holds for unconfigured/partial profiles.
        if (QualityMatcher.IsLossless(existing) && !QualityMatcher.IsLossless(candidate))
        {
            return false;
        }

        if (candidate.BitrateBitsPerSecond is int candidateBitrate and > 0
            && existing.BitrateBitsPerSecond is int existingBitrate and > 0)
        {
            return candidateBitrate >= existingBitrate;
        }

        // Quality is genuinely unknown on at least one side: allow the import rather than silently
        // dropping a file we cannot reason about.
        return true;
    }
}
