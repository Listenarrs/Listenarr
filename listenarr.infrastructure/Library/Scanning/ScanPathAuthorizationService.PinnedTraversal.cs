using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class ScanPathAuthorizationService
{
    private void LogUnavailableCandidate(
        RootCandidate candidate,
        string message,
        string? reason = null)
    {
        var sanitizedPath = LogRedaction.SanitizeFilePath(candidate.Path);
        if (candidate.RequiresEnrollment)
        {
            if (reason == null)
            {
                logger.LogWarning(message, sanitizedPath);
            }
            else
            {
                logger.LogWarning(message, sanitizedPath, reason);
            }
            return;
        }

        if (reason == null)
        {
            logger.LogDebug(message, sanitizedPath);
        }
        else
        {
            logger.LogDebug(message, sanitizedPath, reason);
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor
        OpenRelativeScanRoot(
            PinnedDirectoryCreation.PinnedDirectoryAnchor boundary,
            string boundaryPath,
            string scanPath)
    {
        var current = boundary.Duplicate();
        try
        {
            var relative = Path.GetRelativePath(boundaryPath, scanPath);
            if (relative == ".")
            {
                return current;
            }

            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new InvalidOperationException(
                        "The scan path contains navigation segments outside its configured root.");
                }

                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }
}
