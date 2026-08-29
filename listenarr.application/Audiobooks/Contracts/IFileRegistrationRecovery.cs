using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

/// <summary>
/// Reports whether a committed file-registration move still owns source-cleanup state
/// for an audiobook.
/// </summary>
public interface IFileRegistrationRecoveryProbe
{
    Task<bool> HasBlockingAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);

    Task<bool> HasBlockingBoundaryAsync(
        string boundaryPath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default);
}

public sealed record FileRegistrationRecoveryReceipt(
    Guid OperationId,
    int AudiobookId,
    string SourcePath,
    string DestinationPath);

/// <summary>
/// Reconciles committed file-registration moves whose published destination is already
/// owned by an audiobook but whose original source retirement is still incomplete.
/// </summary>
public interface IFileRegistrationRecoveryService
{
    Task AdoptCommittedAnonymousAsync(
        CancellationToken cancellationToken = default);

    Task ReconcileAsync(CancellationToken cancellationToken = default);

    Task ReconcileAudiobookAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileRegistrationRecoveryReceipt>>
        ReconcileAudiobookWithReceiptsAsync(
            int audiobookId,
            IReadOnlyCollection<string> requestedSourcePaths,
            CancellationToken cancellationToken = default);
}
