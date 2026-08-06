namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record DirectoryObjectIdentityResolution(
    int? Version,
    string? Value,
    string? UnavailableReason)
{
    public bool IsAvailable =>
        Version.HasValue
        && !string.IsNullOrWhiteSpace(Value)
        && string.IsNullOrWhiteSpace(UnavailableReason);

    public static DirectoryObjectIdentityResolution Unavailable(string reason) =>
        new(null, null, reason);
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

    Task<DirectoryObjectIdentityResolution> UpgradeLegacyAsync(
        string path,
        int legacyVersion,
        string legacyValue,
        CancellationToken cancellationToken = default);
}
