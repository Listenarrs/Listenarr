using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

internal interface ICompatibilityFilePublicationRecoveryService
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}

internal sealed class CompatibilityFilePublicationRecoveryService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<CompatibilityFilePublicationRecoveryService> logger)
    : ICompatibilityFilePublicationRecoveryService
{
    public async Task ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        await using var readContext = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var operationIds = await readContext.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.State != CompatibilityFilePublicationState.Completed
                && journal.State != CompatibilityFilePublicationState.NeedsAttention)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => journal.OperationId)
            .ToListAsync(cancellationToken);

        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileOperationAsync(operationId, cancellationToken);
        }
    }

    private async Task ReconcileOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var journal = await context.CompatibilityFilePublicationJournals
            .SingleOrDefaultAsync(
                candidate => candidate.OperationId == operationId,
                cancellationToken);
        if (journal == null
            || journal.State is CompatibilityFilePublicationState.Completed
                or CompatibilityFilePublicationState.NeedsAttention)
        {
            return;
        }

        if (journal.ProtocolVersion
            != CompatibilityFilePublicationProtocol.Current)
        {
            MarkNeedsAttention(
                journal,
                "The compatibility publication protocol is unsupported.");
        }
        else if (journal.State == CompatibilityFilePublicationState.Planned)
        {
            if (File.Exists(journal.DestinationPath))
            {
                MarkNeedsAttention(
                    journal,
                    "A destination exists for an unverified compatibility publication. It was preserved without overwrite or deletion.");
            }
            else if (!ContentMatches(
                journal.SourcePath,
                journal.SourceLength,
                journal.SourceSha256))
            {
                MarkNeedsAttention(
                    journal,
                    "The planned compatibility source is missing or changed.");
            }
            else
            {
                return;
            }
        }
        else if (!ContentMatches(
            journal.DestinationPath,
            journal.TargetLength ?? journal.SourceLength,
            journal.TargetSha256 ?? journal.SourceSha256))
        {
            MarkNeedsAttention(
                journal,
                "The verified compatibility destination is missing or changed.");
        }
        else if (journal.State
            == CompatibilityFilePublicationState.RegistrationCommitted)
        {
            var hasOwner = journal.IsCompanionFile
                || (journal.AudiobookId is int audiobookId
                && await context.AudiobookFiles
                    .AsNoTracking()
                    .AnyAsync(
                        file => file.AudiobookId == audiobookId
                            && (file.Path == journal.DestinationPath
                                || file.CanonicalPath == journal.DestinationPath),
                        cancellationToken));
            if (!hasOwner)
            {
                MarkNeedsAttention(
                    journal,
                    "The committed compatibility destination no longer has its expected audiobook owner.");
            }
            else
            {
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.Error = null;
            }
        }
        else
        {
            // TargetVerified is intentionally resumable only by the original import,
            // which still owns the metadata and destination-planning context.
            return;
        }

        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(cancellationToken);
    }

    private void MarkNeedsAttention(
        CompatibilityFilePublicationJournal journal,
        string reason)
    {
        journal.State = CompatibilityFilePublicationState.NeedsAttention;
        journal.Error = reason;
        logger.LogWarning(
            "Compatibility file publication {OperationId} requires attention: {Reason}",
            journal.OperationId,
            reason);
    }

    private static bool ContentMatches(
        string path,
        long length,
        string sha256)
    {
        try
        {
            using var file = new FileStream(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            if (file.Length != length)
            {
                return false;
            }
            var actual = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(file));
            return string.Equals(actual, sha256, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }
}
