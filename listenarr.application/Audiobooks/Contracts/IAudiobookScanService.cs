using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record AudiobookScanCommand(
    int AudiobookId,
    string ScanRoot,
    PathIdentitySnapshot ScanIdentity,
    bool MoveOwned = false,
    bool AllowReconciliation = true,
    bool IsAuthoritativeScope = true,
    string Source = "Scan",
    string CorrelationId = "");

public sealed record AudiobookScanDiagnostic(
    string Code,
    string? Path,
    string Message);

public sealed record AudiobookScanRemovedFile(
    int Id,
    string? Path);

public sealed record AudiobookScanResult(
    Audiobook Audiobook,
    IReadOnlyList<string> AttributedFiles,
    int CreatedCount,
    IReadOnlyList<AudiobookScanRemovedFile> RemovedFiles,
    string? BasePath,
    bool IsComplete,
    bool ReconciliationPerformed,
    IReadOnlyList<AudiobookScanDiagnostic> Diagnostics);

public interface IAudiobookScanService
{
    Task<AudiobookScanResult> ScanAsync(
        AudiobookScanCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the rows of tracked files inside an audiobook's BasePath when that
    /// folder no longer exists beneath an existing, non-empty, authorized root.
    /// Nothing is enumerated or created. Returns null when the BasePath is not
    /// proven missing that way, so the caller keeps its original refusal.
    /// </summary>
    Task<AudiobookScanResult?> ReconcileMissingBasePathAsync(
        int audiobookId,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> RegisterExistingFileAsync(
        int audiobookId,
        string audiobookBasePath,
        string filePath,
        string source = "manual-import",
        CancellationToken cancellationToken = default);
}
