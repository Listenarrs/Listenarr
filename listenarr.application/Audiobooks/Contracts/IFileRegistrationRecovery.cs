using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

/// <summary>
/// Describes how a nonterminal file-registration publication can progress.
/// </summary>
public enum FileRegistrationRecoveryDisposition
{
    Cleared,
    AutomaticRecovery,
    WaitingForOwnerRetry,
    RequiresOperatorAttention
}

public sealed record FileRegistrationRecoveryBlocker(
    Guid OperationId,
    FileMutationJournalState JournalState,
    FileAction Action,
    int? AudiobookId,
    string OwnerKind,
    bool SourceTouchesBoundary,
    bool DestinationTouchesBoundary,
    FileRegistrationRecoveryDisposition Recoverability,
    string PublicReason);

/// <summary>
/// Reports file-registration publications that still own recovery state.
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

    Task<IReadOnlyList<FileRegistrationRecoveryBlocker>> GetBlockingBoundaryAsync(
        string boundaryPath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default);
}

public sealed record FileRegistrationRecoveryReceipt(
    Guid OperationId,
    int AudiobookId,
    string SourcePath,
    string DestinationPath);

public sealed record FileRegistrationRecoveryStatus(
    Guid OperationId,
    FileMutationJournalState JournalState,
    int? AudiobookId,
    FileRegistrationRecoveryDisposition Disposition,
    bool CanRetry,
    bool CanAbandon,
    string PublicReason);

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

    Task<FileRegistrationRecoveryStatus> RetryAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileRegistrationRecoveryReceipt>>
        ReconcileAudiobookWithReceiptsAsync(
            int audiobookId,
            IReadOnlyCollection<string> requestedSourcePaths,
            CancellationToken cancellationToken = default);
}
