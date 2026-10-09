using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRenameRecoveryReconciler
{
    private async Task SetRecoveryStateAsync(
        Guid operationId,
        FileMutationJournalState state,
        string? detail,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var journal = await db.FileMutationJournals
            .SingleAsync(
                candidate => candidate.OperationId == operationId,
                cancellationToken);
        journal.State = state;
        journal.Error = detail;
        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkNeedsAttentionAsync(
        Guid operationId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var journal = await db.FileMutationJournals
            .SingleAsync(
                candidate => candidate.OperationId == operationId,
                cancellationToken);
        journal.State = FileMutationJournalState.NeedsAttention;
        journal.Error = error;
        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Organize journal {OperationId} requires attention: {Reason}",
            operationId,
            error);
    }
}
