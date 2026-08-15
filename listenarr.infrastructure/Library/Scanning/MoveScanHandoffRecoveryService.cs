using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

public sealed class MoveScanHandoffRecoveryService(
    IScanQueueService scanQueueService,
    IMoveScanHandoffStore handoffStore,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<MoveScanHandoffRecoveryService> logger)
{
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var queryTime = timeProvider.GetUtcNow();
        var ids = await handoffStore.GetClaimableIdsAsync(
            queryTime,
            limit: 100,
            cancellationToken);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await MoveScanHandoffDispatchWorkflow.TryDispatchPendingAsync(
                id,
                ownerPrefix: "scan-recovery",
                knownAudiobook: null,
                beforeEnqueue: null,
                scanQueueService,
                handoffStore,
                scopeFactory,
                timeProvider,
                logger,
                cancellationToken);
        }
    }
}
