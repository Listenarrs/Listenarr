using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record AudiobookScanCommand(
    int AudiobookId,
    string ScanRoot,
    PathIdentitySnapshot ScanIdentity,
    ScanPathPhysicalIdentity ScanPhysicalIdentity,
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

    Task<bool> RegisterExistingFileAsync(
        int audiobookId,
        string audiobookBasePath,
        string filePath,
        string source = "manual-import",
        CancellationToken cancellationToken = default);
}
