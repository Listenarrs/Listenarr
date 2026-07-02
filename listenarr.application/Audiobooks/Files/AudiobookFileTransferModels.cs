namespace Listenarr.Application.Audiobooks.Files;

public sealed record AudiobookFileTransferRequest(
    int SourceAudiobookId,
    int TargetAudiobookId,
    IReadOnlyCollection<int>? FileIds = null);

public enum AudiobookFileTransferStatus
{
    Completed,
    SameAudiobook,
    SourceNotFound,
    TargetNotFound,
    NoMatchingFiles,
    UnknownFileIds
}

public enum AudiobookFileTransferOutcome
{
    /// <summary>The row now belongs to the destination audiobook.</summary>
    Transferred,
    NotOwnedBySource,
    /// <summary>The row carries no proven filesystem identity to transfer under.</summary>
    IdentityUnavailable,
    /// <summary>The persisted identity no longer matches the path; a rescan must repair it first.</summary>
    IdentityStale,
    /// <summary>The destination may not own this identity (conflicting or unresolved ownership).</summary>
    OwnershipConflict,
    /// <summary>The row changed between the ownership check and the re-home.</summary>
    Changed
}

public sealed record AudiobookFileTransferItem(
    int FileId,
    string? Path,
    AudiobookFileTransferOutcome Outcome,
    bool Relocated = false,
    string? Reason = null)
{
    public bool Transferred => Outcome == AudiobookFileTransferOutcome.Transferred;
}

public sealed record AudiobookFileTransferResult(
    AudiobookFileTransferStatus Status,
    IReadOnlyList<AudiobookFileTransferItem> Items,
    string? SourceTitle = null,
    string? TargetTitle = null,
    string? RelocationError = null)
{
    public static AudiobookFileTransferResult Failed(AudiobookFileTransferStatus status) =>
        new(status, []);

    public int TransferredCount => Items.Count(item => item.Transferred);

    public int RelocatedCount => Items.Count(item => item.Relocated);
}
