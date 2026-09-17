using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRegistrationRecoveryService
{
    private async Task AdoptCommittedAnonymousPublicationsAsync(
        int? audiobookId,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var anonymousQuery = db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => (journal.Action == FileAction.Move
                    || journal.Action == FileAction.Copy
                    || journal.Action == FileAction.HardlinkCopy)
                && journal.AudiobookId == null
                && journal.AudiobookFileId == null
                && journal.State == FileMutationJournalState.TargetVerified);
        if (operationId is Guid scopedOperationId)
        {
            anonymousQuery = anonymousQuery.Where(
                journal => journal.OperationId == scopedOperationId);
        }
        var anonymousJournals = await anonymousQuery
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .ToListAsync(cancellationToken);
        if (anonymousJournals.Count == 0)
        {
            return;
        }
        var targetClaims = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => journal.TargetPhysicalObjectIdentity != null
                && journal.State != FileMutationJournalState.RolledBack)
            .ToListAsync(cancellationToken);

        var filesQuery = db.AudiobookFiles.AsNoTracking();
        if (audiobookId is int scopedAudiobookId)
        {
            filesQuery = filesQuery.Where(file => file.AudiobookId == scopedAudiobookId);
        }
        var trackedFiles = await filesQuery.ToListAsync(cancellationToken);
        foreach (var journal in anonymousJournals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = trackedFiles
                .Where(file => RegisteredPathMatches(file, journal.DestinationPath)
                    && RegisteredGenerationMatches(
                        file,
                        journal.TargetPhysicalObjectIdentity))
                .ToList();
            if (matches.Count == 0)
            {
                continue;
            }
            if (targetClaims.Count(candidate =>
                    AnonymousTargetGenerationMatches(candidate, journal)) != 1)
            {
                if (!audiobookId.HasValue)
                {
                    await TryMarkNeedsAttentionAsync(
                        journal.OperationId,
                        journal.State,
                        "Another anonymous registration journal claims the same published target generation.",
                        cancellationToken);
                }
                continue;
            }
            if (matches.Count != 1)
            {
                if (!audiobookId.HasValue)
                {
                    await TryMarkNeedsAttentionAsync(
                        journal.OperationId,
                        journal.State,
                        "Multiple tracked audiobook files claim the anonymous publication target generation.",
                        cancellationToken);
                }
                continue;
            }

            var matchedFile = matches[0];
            var adopted = await TryAdoptAnonymousOwnerAsync(
                db,
                journal,
                matchedFile.AudiobookId,
                cancellationToken);
            if (adopted)
            {
                logger.LogInformation(
                    "Adopted committed anonymous file-registration publication {OperationId} for audiobook {AudiobookId}",
                    journal.OperationId,
                    matchedFile.AudiobookId);
            }
        }
    }

    private async Task<bool> TryAdoptAnonymousOwnerAsync(
        ListenArrDbContext db,
        FileMutationJournal expected,
        int audiobookId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!db.Database.IsRelational())
        {
            var tracked = await db.FileMutationJournals.SingleOrDefaultAsync(
                candidate => candidate.OperationId == expected.OperationId,
                cancellationToken);
            if (tracked == null
                || tracked.AudiobookId != null
                || tracked.AudiobookFileId != null
                || !IsRegistrationPublicationAction(tracked.Action)
                || tracked.State != FileMutationJournalState.TargetVerified
                || !string.Equals(
                    tracked.TargetPhysicalObjectIdentity,
                    expected.TargetPhysicalObjectIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }

            tracked.AudiobookId = audiobookId;
            tracked.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var affected = await db.FileMutationJournals
            .Where(candidate => candidate.OperationId == expected.OperationId
                && candidate.AudiobookId == null
                && candidate.AudiobookFileId == null
                && (candidate.Action == FileAction.Move
                    || candidate.Action == FileAction.Copy
                    || candidate.Action == FileAction.HardlinkCopy)
                && candidate.State == FileMutationJournalState.TargetVerified
                && candidate.TargetPhysicalObjectIdentity
                    == expected.TargetPhysicalObjectIdentity)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.AudiobookId, audiobookId)
                    .SetProperty(candidate => candidate.UpdatedAt, now),
                cancellationToken);
        return affected == 1;
    }

    private async Task ReconcileOrphanedAnonymousPublicationsAsync(
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var journalQuery = db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => (journal.Action == FileAction.Move
                    || journal.Action == FileAction.Copy
                    || journal.Action == FileAction.HardlinkCopy)
                && journal.AudiobookId == null
                && journal.AudiobookFileId == null
                && journal.State != FileMutationJournalState.Completed
                && journal.State != FileMutationJournalState.RolledBack
                && journal.State != FileMutationJournalState.NeedsAttention);
        if (operationId is Guid scopedOperationId)
        {
            journalQuery = journalQuery.Where(
                journal => journal.OperationId == scopedOperationId);
        }
        var journals = await journalQuery
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .ToListAsync(cancellationToken);
        if (journals.Count == 0)
        {
            return;
        }
        var targetClaims = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => journal.TargetPhysicalObjectIdentity != null
                && journal.State != FileMutationJournalState.RolledBack)
            .ToListAsync(cancellationToken);

        var trackedFiles = await db.AudiobookFiles
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        foreach (var journal in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (journal.State is FileMutationJournalState.RegistrationCommitted
                or FileMutationJournalState.SourceDeletionAuthorized
                or FileMutationJournalState.SourceDeleted
                or FileMutationJournalState.OwnerMetadataReconciled)
            {
                await TryMarkNeedsAttentionAsync(
                    journal.OperationId,
                    journal.State,
                    "The registration publication reached an owner-bound state without a durable audiobook owner; its target was preserved.",
                    cancellationToken);
                continue;
            }

            var pathOwners = trackedFiles
                .Where(file => RegisteredPathMatches(file, journal.DestinationPath))
                .ToList();
            var exactOwners = pathOwners
                .Where(file => RegisteredGenerationMatches(
                    file,
                    journal.TargetPhysicalObjectIdentity))
                .ToList();
            if (exactOwners.Count == 1)
            {
                if (journal.State == FileMutationJournalState.TargetVerified)
                {
                    await TryAdoptAnonymousOwnerAsync(
                        db,
                        journal,
                        exactOwners[0].AudiobookId,
                        cancellationToken);
                }
                else
                {
                    await TryMarkNeedsAttentionAsync(
                        journal.OperationId,
                        journal.State,
                        "A tracked audiobook file claimed the target before uncommitted compensation completed; the target was preserved.",
                        cancellationToken);
                }
                continue;
            }
            if (pathOwners.Count > 0)
            {
                await TryMarkNeedsAttentionAsync(
                    journal.OperationId,
                    journal.State,
                    exactOwners.Count > 1
                        ? "Multiple tracked audiobook files claim the anonymous publication target generation."
                        : "A tracked audiobook file claims the publication path with contradictory generation evidence.",
                    cancellationToken);
                continue;
            }
            if (journal.State != FileMutationJournalState.Planned
                && targetClaims.Count(candidate =>
                    AnonymousTargetGenerationMatches(candidate, journal)) != 1)
            {
                await TryMarkNeedsAttentionAsync(
                    journal.OperationId,
                    journal.State,
                    "Another anonymous registration journal claims the same published target generation.",
                    cancellationToken);
                continue;
            }

            try
            {
                var outcome = await fileMover.RollbackUncommittedRegistrationAsync(
                    journal.OperationId);
                if (outcome == UncommittedPublicationRollbackOutcome.RolledBack)
                {
                    logger.LogInformation(
                        "Rolled back orphaned anonymous file-registration publication {OperationId}",
                        journal.OperationId);
                }
            }
            catch (Exception exception) when (
                IsTransientRecoveryFilesystemException(exception))
            {
                logger.LogWarning(
                    exception,
                    "Anonymous file-registration publication {OperationId} remains pending because storage is temporarily unavailable",
                    journal.OperationId);
            }
        }
    }

    private async Task LogRegistrationPublicationSummaryAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var publications = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => journal.AudiobookFileId == null
                && (journal.Action == FileAction.Move
                    || journal.Action == FileAction.Copy
                    || journal.Action == FileAction.HardlinkCopy))
            .Select(journal => new
            {
                journal.State,
                journal.AudiobookId
            })
            .ToListAsync(cancellationToken);
        var completed = publications.Count(journal =>
            journal.State == FileMutationJournalState.Completed);
        var rolledBack = publications.Count(journal =>
            journal.State == FileMutationJournalState.RolledBack);
        var waitingForCommittedOwner = publications.Count(journal =>
            journal.AudiobookId.HasValue
            && FileMutationJournalLifecycle.IsRegistrationPublicationRecoverable(
                journal.State));
        var pendingWithoutOwner = publications.Count(journal =>
            !journal.AudiobookId.HasValue
            && FileMutationJournalLifecycle.IsRegistrationPublicationRecoverable(
                journal.State));
        var needsAttention = publications.Count(journal =>
            FileMutationJournalLifecycle.RequiresOperatorAttention(journal.State));

        logger.LogInformation(
            "Registration publication recovery: {Completed} completed, {RolledBack} rolled back, {WaitingForCommittedOwner} waiting for committed owner recovery, {PendingWithoutOwner} transiently pending without owner, {NeedsAttention} need attention",
            completed,
            rolledBack,
            waitingForCommittedOwner,
            pendingWithoutOwner,
            needsAttention);
    }
}
