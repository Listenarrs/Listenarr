using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record RootFolderPathChangeCommand(
    string TargetPath,
    RootFolderRelocationMode Mode,
    bool DeleteEmptySource,
    string DesiredName,
    bool DesiredIsDefault,
    FileSystemCaseSensitivityMode TargetCaseSensitivityMode,
    string? ExpectedCurrentPath = null);

public sealed record RootFolderRelocationSkippedItemResult(
    int AudiobookId,
    RootFolderRelocationSkipReasonCode ReasonCode);

public sealed record RootFolderMetadataRepairCollisionFile(
    int AudiobookFileId,
    int AudiobookId,
    string RelativePath,
    bool CanRemove);

public sealed record RootFolderMetadataRepairCollisionGroup(
    string TargetRelativePath,
    IReadOnlyList<RootFolderMetadataRepairCollisionFile> Files);

public sealed record RootFolderMetadataRepairDetails(
    Guid RelocationId,
    int AudiobookId,
    string AudiobookTitle,
    RootFolderRelocationSkipReasonCode ReasonCode,
    IReadOnlyList<RootFolderMetadataRepairCollisionGroup> CollisionGroups);

public sealed record RootFolderPathChangeResult(
    Guid? RelocationId,
    int? RootFolderId,
    string CurrentPath,
    string TargetPath,
    RootFolderRelocationStatus Status,
    int TotalJobs,
    int CompletedJobs,
    string? Error,
    TargetIdentityEnrollmentState TargetIdentityEnrollmentState =
        TargetIdentityEnrollmentState.NotRequired,
    IReadOnlyList<int>? SkippedAudiobookIds = null,
    RootFolderRelocationMode Mode = RootFolderRelocationMode.Relocate,
    IReadOnlyList<RootFolderRelocationSkippedItemResult>? SkippedItems = null,
    bool CanAbandon = false);

public interface IRootFolderRelocationService
{
    Task<RootFolderPathChangeResult> StartAsync(
        int rootFolderId,
        RootFolderPathChangeCommand command,
        CancellationToken cancellationToken = default);

    Task<RootFolderPathChangeResult?> GetAsync(
        Guid relocationId,
        CancellationToken cancellationToken = default);

    Task<RootFolderRelocation?> GetActiveForRootAsync(
        int rootFolderId,
        CancellationToken cancellationToken = default);

    Task<bool> IsBoundaryProtectedAsync(
        string path,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default);

    Task<bool> IsAudiobookPathStateProtectedAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);

    Task<RootFolderPathChangeResult> RetryAsync(
        Guid relocationId,
        CancellationToken cancellationToken = default);

    Task<RootFolderPathChangeResult> AbandonUnpublishedAsync(
        Guid relocationId,
        CancellationToken cancellationToken = default);

    Task<RootFolderMetadataRepairDetails?> GetSkippedMetadataRepairDetailsAsync(
        Guid relocationId,
        int audiobookId,
        CancellationToken cancellationToken = default);

    Task<RootFolderMetadataRepairDetails> RemoveSkippedMetadataRepairFileAsync(
        Guid relocationId,
        int audiobookId,
        int audiobookFileId,
        CancellationToken cancellationToken = default);

    Task OnMoveJobStateChangedAsync(
        Guid moveJobId,
        CancellationToken cancellationToken = default);

    Task ReconcileActiveAsync(CancellationToken cancellationToken = default);
}
