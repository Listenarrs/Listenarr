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

        if (journal.ProtocolVersion is not (
                CompatibilityFilePublicationProtocol.RetainOnly or
                CompatibilityFilePublicationProtocol.Current))
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
        else if (journal.ProtocolVersion == CompatibilityFilePublicationProtocol.Current
            && journal.State is
                CompatibilityFilePublicationState.SourceDeleteAuthorized or
                CompatibilityFilePublicationState.SourceQuarantinePlanned or
                CompatibilityFilePublicationState.SourceQuarantined or
                CompatibilityFilePublicationState.SourceDeleted)
        {
            ReconcileInterruptedCleanup(journal);
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
            else if (journal.ProtocolVersion
                    == CompatibilityFilePublicationProtocol.Current
                && journal.CleanupOwner != CompatibilityCleanupOwner.None)
            {
                // The original batch must decide whether every publication succeeded.
                // Startup recovery cannot reconstruct that manifest, so it revokes
                // destructive authority and completes retain-only.
                journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.Error = "Interrupted compatibility batch recovered retain-only.";
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

    private void ReconcileInterruptedCleanup(
        CompatibilityFilePublicationJournal journal)
    {
        if (!ContentMatches(
                journal.DestinationPath,
                journal.TargetLength ?? journal.SourceLength,
                journal.TargetSha256 ?? journal.SourceSha256))
        {
            MarkNeedsAttention(
                journal,
                "The verified destination changed during interrupted source cleanup.");
            return;
        }

        if (journal.State == CompatibilityFilePublicationState.SourceDeleted)
        {
            if (File.Exists(journal.SourcePath)
                || (!string.IsNullOrWhiteSpace(journal.QuarantinePath)
                    && File.Exists(journal.QuarantinePath)))
            {
                journal.SourceDisposition =
                    CompatibilitySourceDisposition.PartialNeedsAttention;
                MarkNeedsAttention(
                    journal,
                    "A source reappeared after source deletion was recorded.");
                return;
            }

            journal.SourceDisposition = CompatibilitySourceDisposition.RetiredByListenarr;
            journal.State = CompatibilityFilePublicationState.Completed;
            journal.Error = null;
            return;
        }

        var sourceMatches = ContentMatches(
            journal.SourcePath,
            journal.SourceLength,
            journal.SourceSha256);
        var quarantineMatches = !string.IsNullOrWhiteSpace(journal.QuarantinePath)
            && ContentMatches(
                journal.QuarantinePath,
                journal.SourceLength,
                journal.SourceSha256);
        if (sourceMatches && !quarantineMatches)
        {
            journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
            journal.State = CompatibilityFilePublicationState.Completed;
            journal.Error = "Interrupted source cleanup recovered retain-only.";
            return;
        }
        if (!sourceMatches && quarantineMatches)
        {
            try
            {
                var quarantinePath = journal.QuarantinePath!;
                using var quarantineParent =
                    PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                        Path.GetDirectoryName(quarantinePath)!);
                using var sourceParent =
                    PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                        Path.GetDirectoryName(journal.SourcePath)!);
                using var quarantined = quarantineParent.OpenExistingFileForStableDelete(
                    Path.GetFileName(quarantinePath));
                quarantined.MoveTo(sourceParent, Path.GetFileName(journal.SourcePath));
                quarantineParent.FlushDirectoryEntry();
                sourceParent.FlushDirectoryEntry();
                journal.SourceDisposition = CompatibilitySourceDisposition.Retained;
                journal.State = CompatibilityFilePublicationState.Completed;
                journal.Error = "Interrupted source cleanup restored from quarantine.";
                return;
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException))
            {
                journal.SourceDisposition =
                    CompatibilitySourceDisposition.PartialNeedsAttention;
                MarkNeedsAttention(
                    journal,
                    "The quarantined source could not be restored without overwrite: "
                    + exception.Message);
                return;
            }
        }

        journal.SourceDisposition = CompatibilitySourceDisposition.PartialNeedsAttention;
        MarkNeedsAttention(
            journal,
            sourceMatches && quarantineMatches
                ? "Both source and quarantine exist after interrupted cleanup."
                : "Both source and quarantine are missing or changed after interrupted cleanup.");
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
