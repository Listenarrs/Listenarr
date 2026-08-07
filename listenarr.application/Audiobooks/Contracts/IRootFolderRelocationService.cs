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
        TargetIdentityEnrollmentState.NotRequired);

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

    Task<RootFolderPathChangeResult> RetryAsync(
        Guid relocationId,
        CancellationToken cancellationToken = default);

    Task OnMoveJobStateChangedAsync(
        Guid moveJobId,
        CancellationToken cancellationToken = default);

    Task ReconcileActiveAsync(CancellationToken cancellationToken = default);
}
