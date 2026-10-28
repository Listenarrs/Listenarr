using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    internal Task EnsureMutationAuthorizedAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken) =>
        EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);

    private Task EnsureMutationAuthorizedAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        CancellationToken cancellationToken) =>
        EnsureMutationAuthorizedAsync(
            request.JobId,
            request.LeaseToken,
            source,
            target,
            request.SourceSemantics,
            request.TargetSemantics,
            cancellationToken);

    private Task EnsureMutationAuthorizedAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken) =>
        executionStore.EnsureMutationAuthorizedAsync(
            jobId,
            leaseToken,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            cancellationToken);
}
