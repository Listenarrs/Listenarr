using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
        var movedBook = await repository.GetByIdAsync(audiobookId);
        if (movedBook != null)
        {
            if (job.RelocationId.HasValue && !string.IsNullOrWhiteSpace(movedBook.NamingRootPath))
            {
                var factory = rewriteScope.ServiceProvider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                var relocation = await db.RootFolderRelocations.FindAsync([job.RelocationId.Value], cancellationToken)
                    ?? throw new MoveNeedsAttentionException("The root relocation context is unavailable.");
                movedBook.NamingRootPath = AudiobookPathReferenceRewriter.RewriteAbsoluteReference(
                    movedBook.NamingRootPath, relocation.SourcePath, relocation.TargetPath, request.SourceSemantics, request.TargetSemantics);
            }
            else if (!job.RelocationId.HasValue) movedBook.SetCustomDestination(publication.Target);
            if (!await repository.UpdateAsync(movedBook))
                throw new MoveNeedsAttentionException("The moved audiobook placement could not be persisted.");
        }
    }
}
