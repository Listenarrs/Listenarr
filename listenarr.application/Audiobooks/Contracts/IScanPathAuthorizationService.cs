using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public enum ScanPathAuthorizationFailure
{
    None,
    InvalidPath,
    ConfigurationUnavailable,
    NoConfiguredRoots,
    OutsideConfiguredRoots,
    IdentityUnavailable
}

public readonly record struct ScanPathPhysicalIdentity(
    string BoundaryObjectIdentity,
    string ScanRootObjectIdentity);

public sealed record ScanPathAuthorizationResult(
    string? Path,
    PathIdentitySnapshot? Identity,
    ScanPathPhysicalIdentity? PhysicalIdentity,
    ScanPathAuthorizationFailure Failure,
    string? Error)
{
    public bool IsAuthorized =>
        !string.IsNullOrWhiteSpace(Path)
        && Identity.HasValue
        && PhysicalIdentity.HasValue
        && !string.IsNullOrWhiteSpace(
            PhysicalIdentity.Value.BoundaryObjectIdentity)
        && !string.IsNullOrWhiteSpace(
            PhysicalIdentity.Value.ScanRootObjectIdentity)
        && Failure == ScanPathAuthorizationFailure.None
        && string.IsNullOrWhiteSpace(Error);

    public static ScanPathAuthorizationResult Authorized(
        string path,
        PathIdentitySnapshot identity,
        ScanPathPhysicalIdentity physicalIdentity) =>
        new(
            path,
            identity,
            physicalIdentity,
            ScanPathAuthorizationFailure.None,
            null);

    public static ScanPathAuthorizationResult Rejected(
        ScanPathAuthorizationFailure failure,
        string error) =>
        new(null, null, null, failure, error);
}

public interface IScanPathAuthorizationService
{
    Task<ScanPathAuthorizationResult> AuthorizeAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<ScanPathAuthorizationResult> ResolveDefaultAsync(
        string? preferredPath,
        CancellationToken cancellationToken = default);
}
