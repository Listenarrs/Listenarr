using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public interface IFileRenameRecoveryProbe
{
    Task<bool> HasBlockingBoundaryAsync(
        string boundaryPath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken = default);

    Task<bool> HasBlockingAsync(
        int audiobookId,
        CancellationToken cancellationToken = default);
}
