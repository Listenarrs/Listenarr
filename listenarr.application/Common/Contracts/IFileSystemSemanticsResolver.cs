using Listenarr.Domain.Common;

namespace Listenarr.Application.Common.Contracts;

public sealed record FileSystemSemanticsResolution(
    FileSystemPathSemantics Semantics,
    PathIdentityState State,
    string BoundaryPath,
    string? Reason = null,
    string? CanonicalPath = null);

public interface IFileSystemSemanticsResolver
{
    ValueTask<FileSystemSemanticsResolution> ResolveAsync(
        string path,
        FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
        CancellationToken cancellationToken = default);
}
