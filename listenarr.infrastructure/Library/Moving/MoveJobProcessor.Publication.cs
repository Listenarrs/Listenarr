using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private async Task CommitMoveOwnerMetadataAsync(
        MoveJob job,
        int audiobookId,
        AudiobookContentMoveRequest request,
        AudiobookContentMoveResult publication,
        CancellationToken cancellationToken)
    {
        if (AfterSourceCleanupBeforeMetadataRewriteForTest != null)
        {
            await AfterSourceCleanupBeforeMetadataRewriteForTest(job);
        }

        await contentMoveService.EnsureMutationAuthorizedAsync(request, cancellationToken);
        await contentMoveService.VerifyTargetBeforeMetadataRewriteAsync(
            request, publication, cancellationToken);
        using var rewriteScope = scopeFactory.CreateScope();
        var repository = rewriteScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
        var targetCaseSensitivityMode = job.TryGetTargetIdentity(out var targetIdentity)
            ? targetIdentity.RequestedMode
            : FileSystemCaseSensitivityMode.Auto;
        await MovedAudiobookPathRewriter.RewriteAsync(
            audiobookId,
            publication.Source,
            publication.Target,
            request.SourceSemantics,
            request.TargetSemantics,
            repository,
            logger,
            cancellationToken,
            publication.TargetPhysicalObjectIdentities,
            targetCaseSensitivityMode);
    }
}
