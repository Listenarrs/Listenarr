using Listenarr.Application.Common;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Deletion;

/// <summary>
/// Centralizes the cancellation-to-commit transition for audiobook deletion.
/// </summary>
public sealed class AudiobookDeletionCommitService(
    IAudiobookRepository repository) : IAudiobookDeletionCommitService
{
    public Task<AudiobookDeletionCommitResult> DeleteAsync(
        int id,
        CancellationToken requestCancellationToken = default) =>
        DeleteAsync(
            id,
            includeFiles: false,
            requestCancellationToken);

    public async Task<AudiobookDeletionCommitResult> DeleteAsync(
        int id,
        bool includeFiles,
        CancellationToken requestCancellationToken = default)
    {
        requestCancellationToken.ThrowIfCancellationRequested();
        var audiobook = await repository.GetForUpdateSnapshotAsync(
            id,
            requestCancellationToken);
        if (audiobook == null)
        {
            return new AudiobookDeletionCommitResult(
                AudiobookDeletionCommitOutcome.NotFound,
                null);
        }

        if (includeFiles && RequiresTrackedFileSnapshot(audiobook))
        {
            audiobook = await repository.GetByIdSnapshotAsync(
                id,
                requestCancellationToken);
            if (audiobook == null)
            {
                return new AudiobookDeletionCommitResult(
                    AudiobookDeletionCommitOutcome.NotFound,
                    null);
            }
        }

        // This is the single request-cancellation fence for the irreversible
        // database deletion. IAudiobookRepository.DeleteByIdAsync is intentionally
        // non-request-cancelable, so a disconnect after this point cannot leave
        // the workflow pretending that a committed delete was rolled back.
        RequestCancellationBoundary.EnterNonCancelablePhase(
            requestCancellationToken);

        var deleted = await repository.DeleteByIdAsync(id);
        return new AudiobookDeletionCommitResult(
            deleted
                ? AudiobookDeletionCommitOutcome.Deleted
                : AudiobookDeletionCommitOutcome.Failed,
            audiobook);
    }

    private static bool RequiresTrackedFileSnapshot(Audiobook audiobook)
    {
        var boundaryPath = !string.IsNullOrWhiteSpace(audiobook.BasePath)
            ? audiobook.BasePath
            : audiobook.FilePath;
        return string.IsNullOrWhiteSpace(boundaryPath)
            || FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                boundaryPath,
                out _,
                out _);
    }
}
