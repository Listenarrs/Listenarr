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
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed record MoveLeaseToken(string Owner, int Generation);

internal sealed record AudiobookContentMoveRequest(
    string Source,
    string Target,
    Guid JobId,
    bool DeleteEmptySource,
    FileSystemPathSemantics SourceSemantics,
    FileSystemPathSemantics TargetSemantics,
    MoveLeaseToken LeaseToken,
    string? SourceCleanupBoundary = null,
    LibraryDirectoryOwnership? TargetDirectoryOwnership = null,
    IReadOnlyDictionary<string, string>? SourcePhysicalObjectIdentities = null,
    Func<double, string, CancellationToken, Task>? ProgressReporter = null)
{
    public string LeaseOwner => LeaseToken.Owner;
    public int LeaseGeneration => LeaseToken.Generation;
}

internal sealed record AudiobookContentMoveResult(
    string Source,
    string Target,
    bool TargetInsideSource,
    bool SourceInsideTarget,
    bool SourceCleanupCompleted,
    IReadOnlyDictionary<string, string> TargetPhysicalObjectIdentities,
    MarkerlessTargetVerificationLease? TargetVerificationLease = null);

internal sealed class MoveNeedsAttentionException(string message) : IOException(message);

internal sealed partial class AudiobookContentMoveService(
    ILogger<AudiobookContentMoveService> logger,
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IMoveFaultInjector? faultInjector = null,
    IMoveExecutionStore? moveExecutionStore = null,
    ILibraryDirectoryOwnershipStore? directoryOwnershipStore = null,
    LibraryDirectoryOwnershipBoundaryAuthorizer? ownershipAuthorizer = null)
{
    private const int MaxCopyAttempts = 5;
    private readonly IMoveExecutionStore executionStore =
        moveExecutionStore ?? new EfMoveExecutionStore(dbContextFactory, timeProvider);
    private readonly ILibraryDirectoryOwnershipStore directoryOwnershipStore =
        directoryOwnershipStore ?? new EfLibraryDirectoryOwnershipStore(dbContextFactory, timeProvider);
    private readonly LibraryDirectoryOwnershipBoundaryAuthorizer ownershipAuthorizer =
        ownershipAuthorizer
        ?? new LibraryDirectoryOwnershipBoundaryAuthorizer(dbContextFactory);

    internal void OnCompletionHandoff(
        Guid jobId,
        CompletionHandoffFaultPoint faultPoint) =>
        faultInjector?.OnCompletionHandoff(jobId, faultPoint);

    public async Task<AudiobookContentMoveResult> MoveContentsAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await EnsureCurrentExecutionProtocolAsync(request.JobId, cancellationToken);

        var source = NormalizeMoveDirectoryEndpoint(request.Source);
        var target = NormalizeMoveDirectoryEndpoint(request.Target);
        var sourceSemantics = request.SourceSemantics;
        var targetSemantics = request.TargetSemantics;
        if (IsFilesystemRoot(source, sourceSemantics)
            || IsFilesystemRoot(target, targetSemantics)
            || FileSystemPathIdentity.AreEquivalentEndpoints(
                source,
                sourceSemantics,
                target,
                targetSemantics))
        {
            throw new MoveNeedsAttentionException(
                "Move source and target must be distinct non-root directories.");
        }

        await ValidateMoveSourceRootForExecutionAsync(
            request.JobId,
            source,
            cancellationToken);
        ValidateMoveTargetRoot(target);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            request.LeaseToken,
            cancellationToken);

        var targetInsideSource = IsSameOrInside(target, source, sourceSemantics);
        var sourceInsideTarget = IsSameOrInside(source, target, targetSemantics);
        return await MoveContentsMarkerlessAsync(
            request,
            source,
            target,
            targetInsideSource,
            sourceInsideTarget,
            cancellationToken);
    }
}
