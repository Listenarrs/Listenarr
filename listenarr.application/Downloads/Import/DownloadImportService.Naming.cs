using System.Globalization;

namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private static AudioMetadata BuildNamingMetadata(
        Audiobook? audiobook,
        AudioMetadata? extractedMetadata,
        string fallbackTitle)
    {
        if (audiobook != null)
        {
            var author = audiobook.Authors is { Count: > 0 }
                ? string.Join(", ", audiobook.Authors)
                : FirstNonEmpty(
                    ChooseAuthorFromMetadata(extractedMetadata),
                    "Unknown Author");

            return new AudioMetadata
            {
                Title = FirstNonEmpty(
                    audiobook.Title,
                    extractedMetadata?.Title,
                    fallbackTitle,
                    "Unknown Title"),
                Subtitle = FirstNonEmpty(
                    audiobook.Subtitle,
                    extractedMetadata?.Subtitle),
                Edition = FirstNonEmpty(
                    audiobook.Edition,
                    extractedMetadata?.Edition),
                Artist = author,
                AlbumArtist = author,
                Album = FirstNonEmpty(
                    extractedMetadata?.Album,
                    audiobook.Title,
                    fallbackTitle),
                Narrator = audiobook.Narrators is { Count: > 0 }
                    ? string.Join(", ", audiobook.Narrators.Where(
                        narrator => !string.IsNullOrWhiteSpace(narrator)))
                    : extractedMetadata?.Narrator,
                Publisher = FirstNonEmpty(
                    audiobook.Publisher,
                    extractedMetadata?.Publisher),
                Language = FirstNonEmpty(
                    audiobook.Language,
                    extractedMetadata?.Language),
                Asin = FirstNonEmpty(
                    audiobook.Asin,
                    extractedMetadata?.Asin),
                Series = FirstNonEmpty(
                    audiobook.Series,
                    extractedMetadata?.Series),
                // Parsed with InvariantCulture: the source value always uses '.' as the
                // decimal separator, so parsing under the server's culture would read a
                // position of "1.5" as 15 wherever '.' is the group separator.
                SeriesPosition = !string.IsNullOrWhiteSpace(audiobook.SeriesNumber)
                    && decimal.TryParse(audiobook.SeriesNumber, NumberStyles.Number, CultureInfo.InvariantCulture, out var seriesPosition)
                        ? seriesPosition
                        : extractedMetadata?.SeriesPosition,
                SeriesPositionRaw = FirstNonEmpty(
                    audiobook.SeriesNumber,
                    extractedMetadata?.SeriesPositionRaw),
                Year = !string.IsNullOrWhiteSpace(audiobook.PublishYear)
                    && int.TryParse(audiobook.PublishYear, out var year)
                        ? year
                        : extractedMetadata?.Year,
                TrackNumber = extractedMetadata?.TrackNumber,
                DiscNumber = extractedMetadata?.DiscNumber,
                BitRate = extractedMetadata?.BitRate,
                Format = extractedMetadata?.Format
            };
        }

        if (extractedMetadata != null)
        {
            if (string.IsNullOrWhiteSpace(extractedMetadata.Title))
            {
                extractedMetadata.Title = fallbackTitle;
            }

            if (string.IsNullOrWhiteSpace(extractedMetadata.Artist))
            {
                extractedMetadata.Artist = FirstNonEmpty(
                    ChooseAuthorFromMetadata(extractedMetadata),
                    "Unknown Author");
            }

            if (string.IsNullOrWhiteSpace(extractedMetadata.AlbumArtist))
            {
                extractedMetadata.AlbumArtist = extractedMetadata.Artist;
            }

            return extractedMetadata;
        }

        return new AudioMetadata
        {
            Title = fallbackTitle,
            Artist = "Unknown Author",
            AlbumArtist = "Unknown Author"
        };
    }

    /// <summary>
    /// The {SeriesNumber} token for a file being imported.
    /// <para>
    /// Prefers the position exactly as the source gave it. A real but non-numeric position
    /// (an omnibus at "1-4") does not survive the decimal parse, and falling through to the
    /// chapter number would write that into the filename as if it were the series number.
    /// </para>
    /// <para>
    /// A parsed position is formatted with InvariantCulture, matching FileNamingService:
    /// ToString() under the server's culture would put a comma into the filename.
    /// </para>
    /// </summary>
    private static string SeriesNumberToken(
        AudioMetadata metadata,
        int? fallbackChapterNumber) =>
        FirstNonEmpty(
            metadata.SeriesPositionRaw,
            metadata.SeriesPosition?.ToString(CultureInfo.InvariantCulture),
            fallbackChapterNumber?.ToString());

    private static string ChooseAuthorFromMetadata(AudioMetadata? metadata)
    {
        if (metadata == null)
        {
            return string.Empty;
        }

        var primary = NonNarratorAuthorCandidate(metadata.Artist, metadata.Narrator);
        var alternate = NonNarratorAuthorCandidate(
            metadata.AlbumArtist,
            metadata.Narrator);

        if (string.IsNullOrWhiteSpace(primary))
        {
            return alternate;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Title)
            && (primary.Contains(metadata.Title, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(metadata.Series)
                    && string.Equals(
                        primary,
                        metadata.Series,
                        StringComparison.OrdinalIgnoreCase))
                || string.Equals(
                    primary,
                    metadata.Title,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return string.IsNullOrWhiteSpace(alternate) ? primary : alternate;
        }

        return primary;
    }

    private static string NonNarratorAuthorCandidate(
        string? candidate,
        string? narrator)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var trimmedCandidate = candidate.Trim();
        if (!string.IsNullOrWhiteSpace(narrator)
            && string.Equals(
                trimmedCandidate,
                narrator.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return trimmedCandidate;
    }

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))
        ?? string.Empty;
}
