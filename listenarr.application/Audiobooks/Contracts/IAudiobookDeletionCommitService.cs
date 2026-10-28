namespace Listenarr.Application.Audiobooks.Contracts;

public enum AudiobookDeletionCommitOutcome
{
    Deleted,
    NotFound,
    Failed
}

public sealed record AudiobookDeletionCommitResult(
    AudiobookDeletionCommitOutcome Outcome,
    Audiobook? Audiobook);

/// <summary>
/// Owns the irreversible database deletion boundary for an audiobook.
/// Request cancellation is authoritative until immediately before the delete
/// commit starts; once that boundary is crossed, the commit must finish.
/// </summary>
public interface IAudiobookDeletionCommitService
{
    Task<AudiobookDeletionCommitResult> DeleteAsync(
        int id,
        CancellationToken requestCancellationToken = default);

    Task<AudiobookDeletionCommitResult> DeleteAsync(
        int id,
        bool includeFiles,
        CancellationToken requestCancellationToken = default);
}
