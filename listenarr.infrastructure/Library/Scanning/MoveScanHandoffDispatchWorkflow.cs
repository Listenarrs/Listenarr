using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal enum MoveScanDispatchOutcome
{
    NotClaimed,
    Dispatched,
    Deferred,
    Superseded,
    Failed
}

internal sealed record MoveScanDispatchResult(
    MoveScanDispatchOutcome Outcome,
    Guid? ScanJobId = null);

internal static class MoveScanHandoffDispatchWorkflow
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public static async Task<MoveScanDispatchResult> TryDispatchPendingAsync(
        Guid handoffId,
        string ownerPrefix,
        Audiobook? knownAudiobook,
        Action<MoveScanHandoffClaim>? beforeEnqueue,
        IScanQueueService scanQueueService,
        IMoveScanHandoffStore handoffStore,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var owner = $"{ownerPrefix}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var claim = await handoffStore.TryClaimAsync(
            handoffId,
            owner,
            now,
            now.Add(LeaseDuration),
            cancellationToken);
        if (claim == null)
        {
            return new MoveScanDispatchResult(MoveScanDispatchOutcome.NotClaimed);
        }

        try
        {
            using var audiobookScope = scopeFactory.CreateScope();
            var audiobookRepository = audiobookScope.ServiceProvider
                .GetRequiredService<IAudiobookRepository>();
            var currentAudiobook = await audiobookRepository.GetPathReferenceSnapshotAsync(
                claim.AudiobookId,
                cancellationToken);
            if (currentAudiobook == null)
            {
                await handoffStore.CompleteAttemptAsync(
                    claim.HandoffId,
                    claim.AttemptGeneration,
                    scanJobId: null,
                    MoveScanTerminalOutcome.Failed,
                    $"Audiobook {claim.AudiobookId} no longer exists.",
                    found: 0,
                    created: 0,
                    scanPath: claim.TargetPath,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return new MoveScanDispatchResult(MoveScanDispatchOutcome.Failed);
            }

            if (!TryCurrentAudiobookTargetsClaim(
                    currentAudiobook.BasePath,
                    claim,
                    out var targetsClaim,
                    out var currentPathError))
            {
                await handoffStore.CompleteAttemptAsync(
                    claim.HandoffId,
                    claim.AttemptGeneration,
                    scanJobId: null,
                    MoveScanTerminalOutcome.Failed,
                    currentPathError,
                    found: 0,
                    created: 0,
                    scanPath: claim.TargetPath,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return new MoveScanDispatchResult(MoveScanDispatchOutcome.Failed);
            }

            if (!targetsClaim)
            {
                const string supersededReason =
                    "A newer audiobook destination superseded this move scan handoff.";
                await handoffStore.CompleteAttemptAsync(
                    claim.HandoffId,
                    claim.AttemptGeneration,
                    scanJobId: null,
                    MoveScanTerminalOutcome.Superseded,
                    supersededReason,
                    found: 0,
                    created: 0,
                    scanPath: claim.TargetPath,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                logger.LogInformation(
                    "Superseded stale move scan handoff {HandoffId} for audiobook {AudiobookId} before filesystem authorization",
                    claim.HandoffId,
                    claim.AudiobookId);
                return new MoveScanDispatchResult(MoveScanDispatchOutcome.Superseded);
            }

            var audiobook = new Audiobook
            {
                Id = claim.AudiobookId,
                Title = knownAudiobook?.Id == claim.AudiobookId
                    ? knownAudiobook.Title
                    : string.Empty,
                BasePath = currentAudiobook.BasePath
            };

            using var authorizationScope = scopeFactory.CreateScope();
            var authorizationService = authorizationScope.ServiceProvider
                .GetRequiredService<IScanPathAuthorizationService>();
            var authorization = await authorizationService.AuthorizeAsync(
                claim.TargetPath,
                cancellationToken);
            if (!authorization.IsAuthorized
                || !authorization.Identity.HasValue
                || !authorization.PhysicalIdentity.HasValue
                || !FileSystemPathIdentity.AreEquivalentEndpoints(
                    claim.TargetPath,
                    claim.TargetIdentity,
                    authorization.Path!,
                    authorization.Identity.Value))
            {
                throw new InvalidOperationException(
                    authorization.Error
                        ?? "The move scan target no longer has its authorized path and physical identity.");
            }

            await AudiobookContentMoveService.VerifyPublishedManifestAsync(
                claim.TargetPath,
                claim.TargetManifest,
                claim.TargetIdentity.Semantics,
                cancellationToken);

            var currentAuthorization = await authorizationService.AuthorizeAsync(
                claim.TargetPath,
                cancellationToken);
            if (!currentAuthorization.IsAuthorized
                || !currentAuthorization.Identity.HasValue
                || !currentAuthorization.PhysicalIdentity.HasValue
                || !FileSystemPathIdentity.AreEquivalentEndpoints(
                    claim.TargetPath,
                    claim.TargetIdentity,
                    currentAuthorization.Path!,
                    currentAuthorization.Identity.Value)
                || currentAuthorization.PhysicalIdentity.Value
                    != authorization.PhysicalIdentity.Value)
            {
                throw new InvalidOperationException(
                    currentAuthorization.Error
                        ?? "The move scan target changed while its durable manifest was being verified.");
            }

            var physicalIdentity =
                currentAuthorization.PhysicalIdentity.Value;
            beforeEnqueue?.Invoke(claim);
            var scanJobId = await scanQueueService.EnqueueMoveHandoffScanAsync(
                audiobook,
                claim,
                physicalIdentity);
            if (!scanJobId.HasValue)
            {
                await handoffStore.ReleaseClaimAsync(
                    claim.HandoffId,
                    claim.LeaseOwner,
                    claim.LeaseGeneration,
                    "A scan for this audiobook is already active; the move scan handoff was deferred.",
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return new MoveScanDispatchResult(MoveScanDispatchOutcome.Deferred);
            }

            logger.LogInformation(
                "Dispatched move scan handoff {HandoffId} attempt {AttemptGeneration} as scan job {ScanJobId}",
                claim.HandoffId,
                claim.AttemptGeneration,
                scanJobId.Value);
            return new MoveScanDispatchResult(
                MoveScanDispatchOutcome.Dispatched,
                scanJobId.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "Move scan handoff {HandoffId} dispatch was canceled internally; releasing the claim for recovery",
                claim.HandoffId);
            try
            {
                await handoffStore.ReleaseClaimAsync(
                    claim.HandoffId,
                    claim.LeaseOwner,
                    claim.LeaseGeneration,
                    exception.Message,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None);
            }
            catch (Exception releaseException) when (WorkerExceptionClassifier.IsNonFatal(releaseException))
            {
                logger.LogDebug(
                    releaseException,
                    "Unable to release internally canceled move scan handoff {HandoffId}; waiting for lease expiry",
                    claim.HandoffId);
            }

            return new MoveScanDispatchResult(MoveScanDispatchOutcome.Failed);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Failed to dispatch move scan handoff {HandoffId}; recovery will retry it",
                claim.HandoffId);
            try
            {
                await handoffStore.ReleaseClaimAsync(
                    claim.HandoffId,
                    claim.LeaseOwner,
                    claim.LeaseGeneration,
                    exception.Message,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
            catch (Exception releaseException) when (WorkerExceptionClassifier.IsNonFatal(releaseException))
            {
                logger.LogDebug(
                    releaseException,
                    "Unable to release move scan handoff {HandoffId}; waiting for lease expiry",
                    claim.HandoffId);
            }

            return new MoveScanDispatchResult(MoveScanDispatchOutcome.Failed);
        }
    }

    private static bool TryCurrentAudiobookTargetsClaim(
        string? currentPath,
        MoveScanHandoffClaim claim,
        out bool targetsClaim,
        out string? error)
    {
        targetsClaim = false;
        error = null;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return true;
        }

        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                currentPath,
                out var canonicalCurrentPath,
                out var pathReason))
        {
            error = pathReason
                ?? "The audiobook's current path is unavailable on this host.";
            return false;
        }

        try
        {
            targetsClaim = FileSystemPathIdentity.AreEquivalent(
                canonicalCurrentPath,
                claim.TargetPath,
                claim.TargetIdentity.Semantics);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            error = $"The audiobook's current path could not be compared to the move scan target: {exception.Message}";
            return false;
        }
    }
}
