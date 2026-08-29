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

public enum ScanPathPhysicalProofKind
{
    DurableGeneration,
    PinnedPathOnly
}

public readonly record struct ScanPathPhysicalIdentity
{
    public ScanPathPhysicalIdentity(
        string boundaryObjectIdentity,
        string scanRootObjectIdentity)
        : this(
            ScanPathPhysicalProofKind.DurableGeneration,
            boundaryObjectIdentity,
            scanRootObjectIdentity)
    {
    }

    private ScanPathPhysicalIdentity(
        ScanPathPhysicalProofKind proofKind,
        string? boundaryObjectIdentity,
        string? scanRootObjectIdentity)
    {
        ProofKind = proofKind;
        BoundaryObjectIdentity = boundaryObjectIdentity;
        ScanRootObjectIdentity = scanRootObjectIdentity;
    }

    public ScanPathPhysicalProofKind ProofKind { get; }
    public string? BoundaryObjectIdentity { get; init; }
    public string? ScanRootObjectIdentity { get; init; }
    public bool HasDurableGenerationProof =>
        ProofKind == ScanPathPhysicalProofKind.DurableGeneration
        && !string.IsNullOrWhiteSpace(BoundaryObjectIdentity)
        && !string.IsNullOrWhiteSpace(ScanRootObjectIdentity);

    public static ScanPathPhysicalIdentity PinnedPathOnly() =>
        new(ScanPathPhysicalProofKind.PinnedPathOnly, null, null);
}

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
        && (PhysicalIdentity.Value.HasDurableGenerationProof
            || PhysicalIdentity.Value.ProofKind
                == ScanPathPhysicalProofKind.PinnedPathOnly)
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
