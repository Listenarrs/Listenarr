using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    // Scan Folder only claims untracked paths, so a file replaced on disk under the same
    // name (a re-grab, a repaired/re-encoded file) kept its old size/codec/bitrate/channels
    // until a separate Rescan Metadata job ran (#1069). Detect a changed on-disk size for
    // each still-present tracked file and re-extract its metadata through the same pinned,
    // ownership-checked refresh path the Rescan Metadata job uses. Size is the only cheap
    // stored signal (AudiobookFile has no mtime); a same-size replacement is deliberately not
    // refreshed here to preserve Scan Folder's cheap, structural character — Rescan Metadata
    // remains the exhaustive re-probe.
    private async Task<int> RefreshChangedTrackedFileMetadataAsync(
        AudiobookScanCommand command,
        PinnedScanAuthority pinnedAuthority,
        Audiobook audiobook,
        ScanDiscoveryResult discovery,
        IReadOnlyCollection<AudiobookFile> existingFiles,
        IReadOnlyDictionary<int, string> resolvedExistingPaths,
        IReadOnlySet<string> attributedPaths,
        ICollection<AudiobookScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var refreshed = 0;
        foreach (var file in existingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Id <= 0
                || !resolvedExistingPaths.TryGetValue(file.Id, out var path)
                || !attributedPaths.Contains(path))
            {
                // Missing or unattributed files are handled by reconciliation, and a
                // freshly claimed file was probed on creation — neither needs a refresh.
                continue;
            }

            long onDiskSize;
            try
            {
                if (!fileSystem.FileExists(path))
                {
                    continue;
                }

                onDiskSize = fileSystem.GetFileLength(path);
            }
            catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
            {
                continue;
            }

            if (file.Size.HasValue && onDiskSize == file.Size.Value)
            {
                // Unchanged on disk; avoid a full metadata re-extraction.
                continue;
            }

            try
            {
                await ValidateCommandAsync(command, cancellationToken);
                ValidateDiscoveredPathParent(
                    command,
                    pinnedAuthority,
                    discovery,
                    path);
                using var registrationLease = OpenPinnedMetadataFile(
                    command,
                    pinnedAuthority,
                    discovery,
                    path);
                if (await fileService.RefreshMetadataAsync(
                        audiobook,
                        file.Id,
                        registrationLease,
                        cancellationToken))
                {
                    refreshed++;
                    diagnostics.Add(new AudiobookScanDiagnostic(
                        "FileMetadataRefreshed",
                        path,
                        "A tracked file changed size on disk; its metadata was re-extracted."));
                }
            }
            catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
            {
                logger.LogWarning(
                    exception,
                    "Failed to refresh changed metadata for tracked file {FileId} of audiobook {AudiobookId}",
                    file.Id,
                    audiobook.Id);
            }
        }

        return refreshed;
    }
}
