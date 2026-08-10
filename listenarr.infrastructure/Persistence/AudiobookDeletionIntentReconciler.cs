using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

public sealed class AudiobookDeletionIntentReconciler(
    IAudiobookDeletionIntentStore intentStore,
    IAudiobookRepository audiobookRepository,
    IAudiobookDeletionCommitService deletionCommitService,
    IAudiobookFilesystemDeleteService filesystemDeleteService,
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
                throw new InvalidOperationException(
                    $"Audiobook deletion intent {intent.Id} requires operator attention: {intent.Error}");
            }

            if (intent.State == AudiobookDeletionIntentState.Planned)
            {
                var audiobook = await audiobookRepository.GetByIdSnapshotAsync(
                    intent.AudiobookId,
                    cancellationToken);
                if (audiobook == null)
                {
                    var reason =
                        "The audiobook row disappeared before its durable filesystem cleanup completed.";
                    await intentStore.MarkNeedsAttentionAsync(
                        intent.Id,
                        reason,
                        CancellationToken.None);
                    throw new InvalidOperationException(reason);
                }

                AudiobookFilesystemDeleteResult result;
                try
                {
                    result = await filesystemDeleteService.DeleteAsync(
                        audiobook,
                        intent.DeleteFolder,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not (
                    OutOfMemoryException or StackOverflowException))
                {
                    await intentStore.RecordErrorAsync(
                        intent.Id,
                        "Filesystem cleanup failed during durable audiobook deletion recovery.",
                        CancellationToken.None);
                    throw new InvalidOperationException(
                        "Durable audiobook deletion recovery could not complete filesystem cleanup safely.",
                        exception);
                }

                foreach (var warning in result.Warnings)
                {
                    logger.LogWarning(
                        "Recovered audiobook deletion {IntentId} completed with warning: {Warning}",
                        intent.Id,
                        warning);
                }
                if (!result.TrackedFileCleanupComplete)
                {
                    await intentStore.RecordErrorAsync(
                        intent.Id,
                        "One or more tracked audiobook file generations remain unresolved after filesystem cleanup recovery.",
                        CancellationToken.None);
                    throw new InvalidOperationException(
                        "Durable audiobook deletion recovery could not prove tracked-file cleanup complete.");
                }
                await intentStore.MarkFilesystemCleanupCompletedAsync(
                    intent.Id,
                    CancellationToken.None);
            }

            var commit = await deletionCommitService.DeleteAsync(
                intent.AudiobookId,
                includeFiles: false,
                CancellationToken.None);
            if (commit.Outcome == AudiobookDeletionCommitOutcome.Failed)
            {
                throw new InvalidOperationException(
                    "Durable audiobook deletion recovery could not commit the database deletion.");
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
