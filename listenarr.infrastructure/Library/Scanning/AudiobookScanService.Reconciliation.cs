using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    private async Task<IReadOnlyList<AudiobookScanRemovedFile>> ReconcileMissingFilesAsync(
        AudiobookScanCommand command,
        PinnedScanAuthority pinnedAuthority,
        Audiobook audiobook,
        IReadOnlyCollection<AudiobookFile> existingFiles,
        IReadOnlyDictionary<int, string> resolvedPaths,
        ScanDiscoveryResult discovery,
        FileSystemPathSemantics semantics,
        ICollection<AudiobookScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!command.AllowReconciliation
            || !command.IsAuthoritativeScope)
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "ReconciliationNotAuthorized",
                command.ScanRoot,
                "This scan scope is not authorized to remove tracked file rows."));
            return [];
        }

        if (!discovery.CanReconcile)
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "ReconciliationSkippedIncompleteScan",
                command.ScanRoot,
                "Filesystem discovery was incomplete; destructive reconciliation was skipped."));
            await TryAddHistoryAsync(new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown",
                EventType = "Scan Incomplete",
                Message = "Scan incomplete; tracked file reconciliation was skipped.",
                Source = command.Source,
                CorrelationId = command.CorrelationId,
                Data = JsonSerializer.Serialize(new
                {
                    command.ScanRoot,
                    Issues = discovery.Issues.Select(issue => new
                    {
                        Kind = issue.Kind.ToString(),
                        issue.Path,
                        issue.Message
                    })
                }),
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return [];
        }

        var removed = new List<AudiobookScanRemovedFile>();
        foreach (var file in existingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolvedPaths.TryGetValue(file.Id, out var resolvedPath))
            {
                continue;
            }

            if (!FileSystemPathIdentity.IsSameOrInside(
                    resolvedPath,
                    command.ScanRoot,
                    semantics))
            {
                diagnostics.Add(new AudiobookScanDiagnostic(
                    "TrackedFileOutsideScope",
                    resolvedPath,
                    "The tracked file lies outside the authoritative scan scope and was preserved."));
                continue;
            }

            ValidateNearestDirectorySnapshot(
                command,
                pinnedAuthority,
                discovery,
                resolvedPath);
            if (PinnedFileExists(command, pinnedAuthority, resolvedPath))
            {
                var isAttributed = discovery.AttributedFiles.Contains(
                    resolvedPath,
                    semantics.Comparer);
                if (!isAttributed)
                {
                    diagnostics.Add(new AudiobookScanDiagnostic(
                        "ExistingFileNotAttributed",
                        resolvedPath,
                        "The file still exists but attribution was inconclusive; its row was preserved."));
                }

                // A live scan may use physical identity transiently to prove that its
                // own pinned snapshot stayed stable, but it never refreshes or
                // backfills AudiobookFile.PhysicalObjectIdentity.
                continue;
            }

            await ValidateCommandAsync(command, cancellationToken);
            ValidateNearestDirectorySnapshot(
                command,
                pinnedAuthority,
                discovery,
                resolvedPath);
            if (PinnedFileExists(command, pinnedAuthority, resolvedPath))
            {
                diagnostics.Add(new AudiobookScanDiagnostic(
                    "TrackedFileReappeared",
                    resolvedPath,
                    "The tracked file reappeared before reconciliation and was preserved."));
                continue;
            }

            if (!await fileRepository.DeletePathStateAsync(
                    file.Id,
                    file.AudiobookId,
                    file.CapturePathState(),
                    cancellationToken))
            {
                diagnostics.Add(new AudiobookScanDiagnostic(
                    "TrackedFileChangedBeforeRemoval",
                    resolvedPath,
                    "The tracked file row changed before verified-missing reconciliation and was preserved."));
                continue;
            }

            removed.Add(new AudiobookScanRemovedFile(file.Id, file.Path));
            await TryAddHistoryAsync(new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown",
                EventType = "File Removed",
                Message = $"Verified missing file removed: {Path.GetFileName(file.Path)}",
                Source = command.Source,
                CorrelationId = command.CorrelationId,
                Data = JsonSerializer.Serialize(new
                {
                    StoredPath = file.Path,
                    ResolvedPath = resolvedPath,
                    file.Size,
                    file.Format,
                    file.Source
                }),
                Timestamp = DateTime.UtcNow
            }, CancellationToken.None);
        }

        return removed;
    }

    private async Task<int> ReconcileLegacyFilePathAsync(
        AudiobookScanCommand command,
        PinnedScanAuthority pinnedAuthority,
        Audiobook audiobook,
        ScanDiscoveryResult discovery,
        FileSystemPathSemantics semantics,
        ICollection<AudiobookScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audiobook.FilePath))
        {
            return 0;
        }

        var storedPath = audiobook.FilePath;
        if (!TryResolveLegacyPath(
                audiobook,
                storedPath,
                semantics,
                out var resolvedPath,
                out var reason))
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "LegacyPathUnresolved",
                storedPath,
                reason ?? "The legacy file path could not be resolved."));
            return 0;
        }

        ValidateNearestDirectorySnapshot(
            command,
            pinnedAuthority,
            discovery,
            resolvedPath);
        if (PinnedFileExists(command, pinnedAuthority, resolvedPath))
        {
            if (!discovery.AttributedFiles.Contains(
                    resolvedPath,
                    semantics.Comparer))
            {
                diagnostics.Add(new AudiobookScanDiagnostic(
                    "LegacyPathNotAttributed",
                    resolvedPath,
                    "The legacy path still exists but was not attributed to this audiobook; it was preserved without being claimed."));
                return 0;
            }

            using var registrationLease = OpenPinnedMetadataFile(
                command,
                pinnedAuthority,
                discovery,
                resolvedPath);
            return await fileService.EnsureAudiobookFileAsync(
                audiobook,
                registrationLease,
                command.Source + "-legacy",
                cancellationToken)
                ? 1
                : 0;
        }

        if (!command.AllowReconciliation
            || !command.IsAuthoritativeScope
            || !discovery.CanReconcile
            || !FileSystemPathIdentity.IsSameOrInside(
                resolvedPath,
                command.ScanRoot,
                semantics))
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "LegacyMissingPathPreserved",
                resolvedPath,
                "The missing legacy file path was outside proven reconciliation authority."));
            return 0;
        }

        await ValidateCommandAsync(command, cancellationToken);
        ValidateNearestDirectorySnapshot(
            command,
            pinnedAuthority,
            discovery,
            resolvedPath);
        if (PinnedFileExists(command, pinnedAuthority, resolvedPath))
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "LegacyPathReappeared",
                resolvedPath,
                "The legacy file reappeared before reconciliation and was preserved."));
            return 0;
        }

        var previousFilePath = audiobook.FilePath;
        var previousFileSize = audiobook.FileSize;
        audiobook.FilePath = null;
        audiobook.FileSize = null;
        if (!await audiobookRepository.UpdateAsync(audiobook))
        {
            audiobook.FilePath = previousFilePath;
            audiobook.FileSize = previousFileSize;
            diagnostics.Add(new AudiobookScanDiagnostic(
                "LegacyPathPersistenceFailed",
                resolvedPath,
                "The verified-missing legacy path could not be cleared from storage."));
            return 0;
        }

        await TryAddHistoryAsync(new History
        {
            AudiobookId = audiobook.Id,
            AudiobookTitle = audiobook.Title ?? "Unknown",
            EventType = "File Removed",
            Message = "Verified missing legacy file path cleared.",
            Source = command.Source,
            CorrelationId = command.CorrelationId,
            Data = JsonSerializer.Serialize(new
            {
                StoredPath = storedPath,
                ResolvedPath = resolvedPath,
                Source = "legacy-reconciliation"
            }),
            Timestamp = DateTime.UtcNow
        }, CancellationToken.None);
        return 0;
    }

    private static bool TryResolveLegacyPath(
        Audiobook audiobook,
        string storedPath,
        FileSystemPathSemantics semantics,
        out string resolvedPath,
        out string? reason)
    {
        resolvedPath = string.Empty;
        reason = null;
        try
        {
            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    storedPath,
                    semantics.Syntax,
                    out _))
            {
                resolvedPath = FileSystemPathIdentity.Canonicalize(
                    storedPath,
                    semantics.Syntax);
                return true;
            }

            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(storedPath, out _))
            {
                reason = "The legacy path uses an unexpected filesystem syntax.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(audiobook.BasePath)
                || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    audiobook.BasePath,
                    storedPath,
                    semantics,
                    out resolvedPath))
            {
                reason = "The relative legacy path cannot be resolved inside BasePath.";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            reason = "The legacy audiobook file path is invalid for this filesystem.";
            return false;
        }
    }
}
