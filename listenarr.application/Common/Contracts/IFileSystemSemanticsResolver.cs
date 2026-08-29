using Listenarr.Domain.Common;

namespace Listenarr.Application.Common.Contracts;

public enum FileSystemSemanticsEvidenceKind
{
    Authoritative,
    BehavioralObservation,
    Unavailable
}

public sealed record FileSystemSemanticsResolution(
    FileSystemPathSemantics Semantics,
    PathIdentityState State,
    string BoundaryPath,
    string? Reason = null,
    string? CanonicalPath = null,
    FileSystemSemanticsEvidenceKind EvidenceKind = FileSystemSemanticsEvidenceKind.Authoritative)
{
    public bool HasDurableMutationSemanticsAuthority =>
        State == PathIdentityState.Valid
        && EvidenceKind == FileSystemSemanticsEvidenceKind.Authoritative;
}

public interface IFileSystemSemanticsResolver
{
    ValueTask<FileSystemSemanticsResolution> ResolveAsync(
        string path,
        FileSystemCaseSensitivityMode mode,
        CancellationToken cancellationToken = default);
}
