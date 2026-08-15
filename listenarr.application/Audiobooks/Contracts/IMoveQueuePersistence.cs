/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record MoveQueueHealthSnapshot(
    int QueueDepth,
    double OldestQueuedAgeSeconds,
    int RetryCount,
    int ExpiredLeaseCount,
    int NeedsAttentionCount);

public enum MoveRequeueOutcome
{
    Requeued,
    AlreadyQueuedWithMatchingIdentity,
    ConflictingActiveJob,
    StaleState,
    NotFound
}

public sealed record RequeueMoveCommand(
    Guid JobId,
    MoveJobStatus ExpectedStatus,
    string SourcePath,
    PathIdentitySnapshot SourceIdentity,
    string TargetPath,
    PathIdentitySnapshot TargetIdentity,
    string DeduplicationKey,
    DateTimeOffset UpdatedAt);

public sealed record MoveRequeueResult(
    MoveRequeueOutcome Outcome,
    MoveJob? Job = null);

public interface IMoveQueuePersistence
{
    Task<MoveJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<MoveJob?> GetActiveByKeyAsync(string deduplicationKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MoveJob>> GetActiveAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MoveJob>> GetRecoveryCandidatesByAudiobookAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MoveJob>> GetRecoveryCandidatesAsync(
        CancellationToken cancellationToken = default);

    Task ReconcileIdentityKeysAsync(CancellationToken cancellationToken = default);

    Task<MoveQueueHealthSnapshot> GetHealthAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task AddAsync(MoveJob job, CancellationToken cancellationToken = default);

    Task<MoveRequeueResult> RequeueAsync(
        RequeueMoveCommand command,
        CancellationToken cancellationToken = default);

    Task<bool> MarkNeedsAttentionAsync(
        Guid id,
        MoveJobStatus expectedStatus,
        string error,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateStatusAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        MoveJobStatus status,
        MoveJobPhase phase,
        string? error,
        MoveFailureKind failureKind,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryIncrementAttemptAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<MoveRetryScheduleResult?> ScheduleRetryAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        int expectedAttemptCount,
        DateTimeOffset updatedAt,
        DateTimeOffset nextAttemptAt,
        int maxAttempts,
        string error,
        CancellationToken cancellationToken = default);

    Task<int?> TryClaimAsync(
        Guid id,
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<MoveHeartbeatOutcome> HeartbeatAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);
}
