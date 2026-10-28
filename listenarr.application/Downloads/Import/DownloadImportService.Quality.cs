namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private static string? ResolveBestExistingQuality(
        Audiobook audiobook,
        QualityProfile? profile)
    {
        string? bestExisting = null;
        if (audiobook.Files == null || audiobook.Files.Count == 0)
        {
            return bestExisting;
        }

        foreach (var file in audiobook.Files)
        {
            var quality = file.Format ?? string.Empty;
            if (file.Bitrate.HasValue)
            {
                var kbps = file.Bitrate.Value / 1000;
                if (kbps >= 320) quality = "MP3 320kbps";
                else if (kbps >= 256) quality = "MP3 256kbps";
                else if (kbps >= 192) quality = "MP3 192kbps";
                else if (kbps >= 128) quality = "MP3 128kbps";
            }

            if (string.IsNullOrEmpty(quality) && !string.IsNullOrEmpty(file.Path))
            {
                quality = ImportQualityEvaluator.Determine(null, file.Path);
            }

            if (string.IsNullOrEmpty(bestExisting))
            {
                bestExisting = quality;
            }
            else if (!string.IsNullOrEmpty(quality)
                     && profile != null
                     && ImportQualityEvaluator.IsAcceptable(
                         quality,
                         bestExisting,
                         profile))
            {
                bestExisting = quality;
            }
        }

        return bestExisting;
    }
}
