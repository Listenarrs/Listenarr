namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record MoveSourceCleanupAuthorization(
    MoveSourceCleanupMode Mode,
    int? SourceRootFolderId,
    int? SourcePolicyRevision,
    int? TargetRootFolderId,
    int? TargetPolicyRevision,
    bool SourceIsManagedRoot,
    string Message,
    int? SourceStorageContractRevision = null,
    int? TargetStorageContractRevision = null,
    bool ForceCopyAndRetainSource = false)
{
    public bool DeletesSourceAfterVerifiedCopy =>
        Mode == MoveSourceCleanupMode.DeleteAfterVerifiedCopy;
}

public interface IMoveSourceCleanupPolicyResolver
{
    Task<MoveSourceCleanupAuthorization> ResolveAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken = default);

    Task<bool> IsCurrentAsync(
        MoveSourceCleanupAuthorization authorization,
        CancellationToken cancellationToken = default);
}
