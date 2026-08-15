namespace Listenarr.Application.Audiobooks.Files;

public enum AudiobookFileClaimOutcome
{
    Created,
    AlreadyOwnedByAudiobook,
    OwnedByOtherAudiobook,
    IdentityConflict,
    IdentityUnavailable
}

public sealed record AudiobookFileClaimResult(
    AudiobookFileClaimOutcome Outcome,
    AudiobookFile? File = null,
    string? Reason = null)
{
    public bool Created => Outcome == AudiobookFileClaimOutcome.Created;
}

public enum AudiobookFileOwnershipCheckOutcome
{
    Available,
    AlreadyOwnedByAudiobook,
    OwnedByOtherAudiobook,
    IdentityConflict,
    IdentityUnavailable
}

public sealed record AudiobookFileOwnershipCheckResult(
    AudiobookFileOwnershipCheckOutcome Outcome,
    AudiobookFile? ExistingFile = null,
    string? Reason = null);

public sealed record AudiobookFileIdentityReconciliationResult(
    int Processed,
    int Valid,
    int Conflicted,
    int Unavailable);
