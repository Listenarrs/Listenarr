using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

/// <summary>
/// Replays durable audiobook deletion intents during startup recovery.
/// <para>
/// Any intent in NeedsAttention is reported and stepped over rather than thrown
/// on. It is a row waiting for a human, which says nothing about whether the
/// library filesystem is usable, so it must not fail the recovery phase and
/// disable filesystem mutations across the whole install. The reconciler parks an
/// intent that way itself when the audiobook row it names has already gone: the
/// paths that intent was going to clean went with that row, so no later pass can
/// do better. A filesystem that is genuinely failing is a separate condition and
/// still fails the phase.
/// </para>
/// </summary>
public sealed class AudiobookDeletionIntentReconciler(
    IAudiobookDeletionIntentStore intentStore,
    IAudiobookRepository audiobookRepository,
    IAudiobookDeletionCommitService deletionCommitService,
    IAudiobookFilesystemDeleteService filesystemDeleteService,
    ILogger<AudiobookDeletionIntentReconciler> logger) : IAudiobookDeletionIntentReconciler
{
    private const string MissingAudiobookReason =
        "The audiobook row disappeared before its durable filesystem cleanup completed.";

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var intents = await intentStore.GetActiveAsync(cancellationToken);
        foreach (var intent in intents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (intent.State == AudiobookDeletionIntentState.NeedsAttention)
            {
                ReportParkedIntent(intent, intent.Error);
                continue;
            }

            if (intent.State == AudiobookDeletionIntentState.Planned)
            {
                var audiobook = await audiobookRepository.GetByIdSnapshotAsync(
                    intent.AudiobookId,
                    cancellationToken);
                if (audiobook == null)
                {
                    await intentStore.MarkNeedsAttentionAsync(
                        intent.Id,
                        MissingAudiobookReason,
                        CancellationToken.None);
                    ReportParkedIntent(intent, MissingAudiobookReason);
                    continue;
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
                catch (Exception exception) when (
                    IsTransientRecoveryFilesystemException(exception))
                {
                    await intentStore.RecordErrorAsync(
                        intent.Id,
                        "Filesystem cleanup is temporarily unavailable during durable audiobook deletion recovery.",
                        CancellationToken.None);
                    logger.LogWarning(
                        exception,
                        "Audiobook deletion intent {IntentId} remains pending because filesystem cleanup is temporarily unavailable",
                        intent.Id);
                    continue;
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
                        "One or more tracked audiobook file generations remain pending after filesystem cleanup recovery.",
                        CancellationToken.None);
                    logger.LogWarning(
                        "Audiobook deletion intent {IntentId} remains pending because tracked-file cleanup is not yet complete",
                        intent.Id);
                    continue;
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

    private void ReportParkedIntent(AudiobookDeletionIntent intent, string? reason) =>
        logger.LogError(
            "Audiobook deletion intent {IntentId} for audiobook {AudiobookId} cannot be recovered and was skipped so the rest of library filesystem startup can continue. Files belonging to that audiobook may still be on disk and need clearing by hand. Reason: {Reason}",
            intent.Id,
            intent.AudiobookId,
            string.IsNullOrWhiteSpace(reason)
                ? "No reason was recorded against the intent."
                : reason);

    private static bool IsTransientRecoveryFilesystemException(Exception exception)
    {
        if (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        if (exception is System.ComponentModel.Win32Exception native)
        {
            return native.NativeErrorCode is 5 or 13 or 16 or 30 or 32 or 33;
        }

        return exception is InvalidOperationException { InnerException: not null }
            && IsTransientRecoveryFilesystemException(exception.InnerException);
    }
}
