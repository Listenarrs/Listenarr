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
    string DestinationPath,
    bool SourceRetained = false,
    long? SourceLength = null,
    string? SourceSha256 = null);

public sealed record FileRegistrationRecoveryStatus(
    Guid OperationId,
    FileMutationJournalState JournalState,
    int? AudiobookId,
    FileRegistrationRecoveryDisposition Disposition,
    bool CanRetry,
    bool CanAbandon,
    string PublicReason);

public sealed record RegistrationFileObservation(string Status, long? Length, string? Sha256,
    string? ObjectIdentity = null, DateTime? LastWriteTimeUtc = null);
public sealed record RegistrationTrackedFile(int Id, int AudiobookId, string? Path);
public sealed record FileRegistrationReview(
    Guid OperationId, int? AudiobookId, string Action, int ProtocolVersion, string State,
    string? Error, string SourcePath, string DestinationPath,
    RegistrationFileObservation Source, RegistrationFileObservation Destination,
    IReadOnlyList<RegistrationTrackedFile> Registrations, string ContentEvidence,
    bool CanResolve, string ReviewToken, string? ResolutionNote, string? ResolvedBy,
    DateTime? ResolvedAtUtc);

/// <summary>
/// Reconciles committed file-registration moves whose published destination is already
/// owned by an audiobook but whose original source retirement is still incomplete.
/// </summary>
public interface IFileRegistrationRecoveryService
{
    Task<IReadOnlyList<FileRegistrationReview>> ListReviewsAsync(CancellationToken cancellationToken = default);
    Task<FileRegistrationReview> InspectAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<FileRegistrationReview> ResolveAsync(Guid operationId, string reviewToken, string note,
        bool acknowledgeIncompleteContent, string actor, CancellationToken cancellationToken = default);
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
