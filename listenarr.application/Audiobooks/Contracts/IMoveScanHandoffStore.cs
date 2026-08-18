using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record MoveCompletionCommit(
    Guid MoveJobId,
    string LeaseOwner,
    int LeaseGeneration,
    int AudiobookId,
    string? AudiobookTitle,
    string Source,
    string Target,
    DateTimeOffset Now);

public sealed record MoveCompletionCommitResult(
    MoveScanHandoff Handoff,
    History MoveHistory,
    bool MoveHistoryCreated,
    bool HandoffCreated);

public sealed record MoveScanHandoffClaim(
    Guid HandoffId,
    Guid MoveJobId,
    int AudiobookId,
    string TargetPath,
    PathIdentitySnapshot TargetIdentity,
    IReadOnlyList<MoveJobEntry> TargetManifest,
    int AttemptGeneration,
    string LeaseOwner,
    int LeaseGeneration);

public enum MoveScanTerminalOutcome
{
    Succeeded,
    Failed,
    Superseded
}

public enum MoveScanAttemptOutcome
{
    Completed,
    Failed,
    Superseded
}

public sealed record MoveScanAttemptResult(
    MoveScanAttemptOutcome Outcome,
    string? Error);

public enum MoveScanLeaseRenewalOutcome
{
    Renewed,
    Completed,
    Failed,
    Superseded
}

public sealed record MoveScanLeaseRenewalResult(
    MoveScanLeaseRenewalOutcome Outcome,
    string? Error = null);

public interface IMoveScanHandoffStore
{
    Task<MoveCompletionCommitResult> CommitMoveCompletionAsync(
        MoveCompletionCommit command,
        CancellationToken cancellationToken = default);

    Task<MoveCompletionCommitResult> CommitMoveCompletionAsync(
        MoveCompletionCommit command,
        Func<CancellationToken, Task<RegistrationPublicationMatchOutcome>>
            commitValidation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetClaimableIdsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default);

    Task<MoveScanHandoffClaim?> TryClaimAsync(
        Guid handoffId,
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<bool> MarkDispatchedAsync(
        Guid handoffId,
        string leaseOwner,
        int leaseGeneration,
        Guid scanJobId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<MoveScanLeaseRenewalResult> RenewAttemptLeaseAsync(
        Guid handoffId,
        int attemptGeneration,
        Guid scanJobId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<MoveScanAttemptResult> CompleteAttemptAsync(
        Guid handoffId,
        int attemptGeneration,
        Guid? scanJobId,
        MoveScanTerminalOutcome outcome,
        string? error,
        int found,
        int created,
        string? scanPath,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseClaimAsync(
        Guid handoffId,
        string leaseOwner,
        int leaseGeneration,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> RequeueAsync(
        Guid handoffId,
        Guid expectedScanJobId,
        int expectedAttemptGeneration,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
