namespace Listenarr.Application.Audiobooks.Contracts;

public enum RootFolderStorageState
{
    Healthy,
    Missing,
    Changed,
    Unavailable,
    Unconfirmed
}

public enum RootFolderStorageReason
{
    None,
    PathMissing,
    ForeignPathSyntax,
    AccessDenied,
    IdentityUnsupported,
    IdentityMismatch,
    IdentityUnstable,
    FilesystemSemanticsUnavailable,
    FilesystemSemanticsChanged,
    NoAuthorizedIdentity,
    InvalidPath,
    Unknown
}

public sealed record RootFolderStorageObservation(
    RootFolderStorageState State,
    RootFolderStorageReason Reason,
    string? Message,
    bool CanConfirmCurrentFolder,
    bool CanChangePath,
    bool CanMutateFilesystem,
    string? ConfirmationToken);

public interface IRootFolderStorageHealthResolver
{
    Task<RootFolderStorageObservation> ResolveAsync(
        RootFolder root,
        CancellationToken cancellationToken = default);
}
