using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

public sealed class AudiobookDeletionIntentReconciler(
    IAudiobookDeletionIntentStore intentStore,
    IAudiobookRepository audiobookRepository,
    IAudiobookDeletionCommitService deletionCommitService,
    ILogger<AudiobookDeletionIntentReconciler> logger) : IAudiobookDeletionIntentReconciler
{
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var intents = await intentStore.GetActiveAsync(cancellationToken);
        foreach (var intent in intents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (intent.State == AudiobookDeletionIntentState.NeedsAttention)
            {
                logger.LogWarning(
                    "Audiobook deletion intent {IntentId} remains scoped to operator attention: {Reason}",
                    intent.Id,
                    intent.Error);
                continue;
            }

            if (intent.State == AudiobookDeletionIntentState.Planned)
            {
                var audiobook = await audiobookRepository.GetByIdSnapshotAsync(
                    intent.AudiobookId,
                    cancellationToken);
                if (audiobook == null)
                {
                    var reason =
                        "The audiobook row disappeared before explicit filesystem cleanup completed.";
                    await intentStore.MarkNeedsAttentionAsync(
                        intent.Id,
                        reason,
                        CancellationToken.None);
                    logger.LogWarning(
                        "Audiobook deletion intent {IntentId} requires scoped repair: {Reason}",
                        intent.Id,
                        reason);
                    continue;
                }

                // A Planned intent proves user intent existed, but the pinned
                // filesystem proof that authorized destructive mutation was
                // process-local. Startup recovery must not reacquire that proof
                // and delete on the user's behalf. Preserve the source and let a
                // new explicit delete request reacquire live proof.
                await intentStore.RecordErrorAsync(
                    intent.Id,
                    "Filesystem deletion was not resumed after restart because live delete proof expires at the process boundary. Retry the explicit delete to reacquire live proof.",
                    CancellationToken.None);
                logger.LogWarning(
                    "Audiobook deletion intent {IntentId} retained filesystem content after restart; explicit retry is required",
                    intent.Id);
                continue;
            }

            var commit = await deletionCommitService.DeleteAsync(
                intent.AudiobookId,
                includeFiles: false,
                CancellationToken.None);
            if (commit.Outcome == AudiobookDeletionCommitOutcome.Failed)
            {
                await intentStore.RecordErrorAsync(
                    intent.Id,
                    "Filesystem cleanup completed, but the database deletion could not be committed during recovery.",
                    CancellationToken.None);
                logger.LogWarning(
                    "Audiobook deletion intent {IntentId} remains pending because database deletion could not be committed",
                    intent.Id);
                continue;
            }

            await intentStore.MarkCompletedAsync(
                intent.Id,
                CancellationToken.None);
            logger.LogInformation(
                "Recovered durable audiobook deletion {IntentId} for audiobook {AudiobookId}",
                intent.Id,
                intent.AudiobookId);
        }
    }
}
