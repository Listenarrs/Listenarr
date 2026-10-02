using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    /// <summary>
    /// Whether a candidate may be registered as an audiobook file, deciding from its content where
    /// its extension cannot settle the question.
    /// </summary>
    /// <remarks>
    /// Two tiers decide admission. The pre-filter upstream is extension-only and lets an ambiguous
    /// container such as `.mp4` through on its name alone; this is the one content gate, and it is
    /// what such a container then has to pass. An always-audio extension short-circuits here and is
    /// never probed for admission.
    ///
    /// Cover art is reported as a stream of type video with `attached_pic` set, so it is excluded
    /// from the video test deliberately: counting it would refuse most real audiobooks.
    ///
    /// The probe costs nothing extra at this point, because registration already extracts metadata
    /// for every file it records.
    /// </remarks>
    private bool QualifiesAsAudioContent(string filePath, AudioMetadata? meta, int audiobookId)
    {
        if (FileUtils.IsAudioFile(filePath) || (meta != null && FileUtils.IsProbedAudioContent(meta)))
        {
            return true;
        }

        logger.LogInformation(
            "Skipping audiobook file registration because the probed container carries no audio-only content for audiobook {AudiobookId}: {Path}",
            audiobookId,
            LogRedaction.SanitizeFilePath(filePath));
        return false;
    }
}
