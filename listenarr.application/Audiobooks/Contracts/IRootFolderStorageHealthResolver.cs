namespace Listenarr.Application.Audiobooks.Contracts;

public enum RootFolderStorageState
{
    Healthy,
    Limited,
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
    MutationSemanticsUnproven,
    ReadOnlyFilesystem,
    MutationCapabilityUnavailable,
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
    string? ConfirmationToken,
    string? Detail = null,
    bool CanPublishNewFiles = false,
    bool CanRetireWithDurableIdentity = false,
    bool CanRetireAfterVerifiedCopy = false)
{
    public bool CanPublishAdditively =>
        CanPublishNewFiles || CanMutateFilesystem;

    public bool CanRetireDurably =>
        CanRetireWithDurableIdentity || CanMutateFilesystem;

    public bool CanRetireVerifiedSource =>
        CanRetireAfterVerifiedCopy || CanMutateFilesystem;

    public bool CanReadFilesystem =>
        State is RootFolderStorageState.Healthy or RootFolderStorageState.Limited;

    public bool CanScanFilesystem =>
        State is RootFolderStorageState.Healthy or RootFolderStorageState.Limited;
}

public interface IRootFolderStorageHealthResolver
{
    Task<RootFolderStorageObservation> ResolveAsync(
        RootFolder root,
        CancellationToken cancellationToken = default);
}
