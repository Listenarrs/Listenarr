namespace Listenarr.Application.Audiobooks.Contracts;

public enum DirectoryObjectIdentityFailureKind
{
    None,
    Missing,
    ForeignPathSyntax,
    AccessDenied,
    IdentityUnsupported,
    IdentityMismatch,
    IdentityUnstable,
    InvalidPath,
    Unknown
}

public sealed record DirectoryObjectIdentityResolution(
    int? Version,
    string? Value,
    string? UnavailableReason)
{
    public DirectoryObjectIdentityFailureKind FailureKind { get; init; } =
        DirectoryObjectIdentityFailureKind.None;

    public bool IsAvailable =>
        Version.HasValue
        && !string.IsNullOrWhiteSpace(Value)
        && string.IsNullOrWhiteSpace(UnavailableReason);

    public static DirectoryObjectIdentityResolution Unavailable(
        string reason,
        DirectoryObjectIdentityFailureKind failureKind = DirectoryObjectIdentityFailureKind.Unknown) =>
        new(null, null, reason)
        {
            FailureKind = failureKind
        };
}

public interface IDirectoryObjectIdentityResolver
{
    Task<DirectoryObjectIdentityResolution> ResolveAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<DirectoryObjectIdentityResolution> ResolveExistingAsync(
        string path,
        int expectedVersion,
        string expectedValue,
        CancellationToken cancellationToken = default);

}
