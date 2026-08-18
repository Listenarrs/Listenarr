using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

/// <summary>
/// Reconciles generation-fenced organize/rename and companion-file move journals
/// before ordinary file-identity startup reconciliation. Companion journals have no
/// audiobook-file metadata rewrite; reaching Completed is their terminal owner state.
/// </summary>
public sealed partial class FileRenameRecoveryReconciler(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFileMover fileMover,
    IAudiobookFilePathIdentityResolver identityResolver,
    IFileSystemSemanticsResolver semanticsResolver,
    TimeProvider timeProvider,
    ILogger<FileRenameRecoveryReconciler> logger) : IFileRenameRecoveryReconciler
{
    internal Func<Guid, Task>? AfterInitialOwnerBindingLoadedForTestAsync { get; set; }
    internal Func<Guid, Task>? BeforeOwnerMetadataCommitForTestAsync { get; set; }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCurrentOwnerRecoveryProtocolAsync(cancellationToken);
        await using var readContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var attentionOperationId = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.Action == FileAction.Move
                && journal.AudiobookId != null
                && journal.AudiobookFileId != null
                && journal.State == FileMutationJournalState.NeedsAttention)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => (Guid?)journal.OperationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (attentionOperationId.HasValue)
        {
            throw new InvalidOperationException(
                $"Owner-bound file organize journal {attentionOperationId.Value} requires operator repair before filesystem mutations can resume.");
        }

        var operationIds = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.Action == FileAction.Move
                && journal.AudiobookId != null
                && journal.AudiobookFileId != null
                && (journal.AudiobookFileId == FileMutationOwner.CompanionFile
                    ? journal.State != FileMutationJournalState.Completed
                    : journal.State != FileMutationJournalState.OwnerMetadataReconciled))
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
        FileMutationJournal journal;
        Audiobook audiobook;
        AudiobookFile? audiobookFile;
        int ownerAudiobookId;
        int ownerAudiobookFileId;
        await using (var context = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            journal = await context.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId, cancellationToken);
            if (journal.State is FileMutationJournalState.OwnerMetadataReconciled
                or FileMutationJournalState.NeedsAttention)
            {
                return;
            }

            if (!journal.AudiobookId.HasValue || !journal.AudiobookFileId.HasValue)
            {
                return;
            }

            ownerAudiobookId = journal.AudiobookId.Value;
            ownerAudiobookFileId = journal.AudiobookFileId.Value;
            var isCompanionFile = FileMutationOwner.IsCompanionFile(ownerAudiobookFileId);
            audiobook = await context.Audiobooks
                .AsNoTracking()
                .Include(candidate => candidate.Files)
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == journal.AudiobookId.Value,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "An owned file-mutation journal references a missing audiobook before metadata reconciliation.");
            audiobookFile = ownerAudiobookFileId == 0 || isCompanionFile
                ? null
                : audiobook.Files?.SingleOrDefault(file => file.Id == ownerAudiobookFileId)
                    ?? throw new InvalidOperationException(
                        "An owned file-mutation journal references a missing audiobook file before metadata reconciliation.");
        }

        if (AfterInitialOwnerBindingLoadedForTestAsync != null)
        {
            await AfterInitialOwnerBindingLoadedForTestAsync(operationId);
        }

        if (journal.State < FileMutationJournalState.Completed)
        {
            bool resumed;
            try
            {
                resumed = FileMutationOwner.IsCompanionFile(ownerAudiobookFileId)
                    ? await fileMover.PerformActionOn(
                        FileAction.Move,
                        journal.SourcePath,
                        journal.DestinationPath,
                        journal.OperationId,
                        ownerAudiobookId,
                        FileMutationOwner.CompanionFile)
                    : audiobookFile == null
                        ? await fileMover.PerformActionOn(
                            FileAction.Move,
                            journal.SourcePath,
                            journal.DestinationPath,
                            journal.OperationId,
                            ownerAudiobookId,
                            audiobookFileId: 0)
                        : await fileMover.MoveFilePreservingPhysicalIdentityAsync(
                            journal.SourcePath,
                            journal.DestinationPath,
                            journal.SourcePhysicalObjectIdentity,
                            journal.OperationId,
                            ownerAudiobookId,
                            audiobookFile.Id);
            }
            catch (Exception exception) when (IsTransientRecoveryFilesystemException(exception))
            {
                logger.LogWarning(
                    exception,
                    "Owner-bound file recovery {OperationId} remains pending because its filesystem source or target is temporarily unavailable",
                    operationId);
                return;
            }

            if (!resumed)
            {
                await MarkNeedsAttentionAsync(
                    operationId,
                    "The interrupted organize file mutation could not be resumed safely.",
                    cancellationToken);
                return;
            }
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        journal = await db.FileMutationJournals
            .SingleAsync(candidate => candidate.OperationId == operationId, cancellationToken);
        if (journal.State == FileMutationJournalState.OwnerMetadataReconciled)
        {
            return;
        }
        if (journal.State != FileMutationJournalState.Completed
            || string.IsNullOrWhiteSpace(journal.TargetPhysicalObjectIdentity))
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The interrupted organize journal did not reach a verified completed target.",
                cancellationToken);
            return;
        }
        if (journal.AudiobookId != ownerAudiobookId
            || journal.AudiobookFileId != ownerAudiobookFileId)
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The interrupted file-mutation journal owner binding changed during recovery.",
                cancellationToken);
            return;
        }
        if (FileMutationOwner.IsCompanionFile(ownerAudiobookFileId))
        {
            logger.LogInformation(
                "Recovered interrupted companion-file move journal {OperationId} for audiobook {AudiobookId}",
                operationId,
                ownerAudiobookId);
            return;
        }

        var trackedAudiobook = await db.Audiobooks
            .Include(candidate => candidate.Files)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == ownerAudiobookId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The audiobook disappeared before its completed organize journal could be reconciled.");
        var trackedFile = ownerAudiobookFileId == 0
            ? null
            : trackedAudiobook.Files?.SingleOrDefault(file => file.Id == ownerAudiobookFileId)
                ?? throw new InvalidOperationException(
                    "The audiobook file disappeared before its completed organize journal could be reconciled.");

        var targetGeneration = ProbeTargetGeneration(journal);
        if (targetGeneration == GenerationMatchOutcome.Unavailable)
        {
            logger.LogWarning(
                "Completed organize journal {OperationId} remains pending because its destination generation is temporarily unavailable",
                operationId);
            return;
        }
        if (targetGeneration == GenerationMatchOutcome.Mismatch)
        {
            var sourceGeneration = ProbeSourceGeneration(journal);
            if (sourceGeneration == GenerationMatchOutcome.Unavailable)
            {
                logger.LogWarning(
                    "Completed organize journal {OperationId} remains pending because its compensation source generation is temporarily unavailable",
                    operationId);
                return;
            }

            var ownerPointsToSource = sourceGeneration == GenerationMatchOutcome.Match
                ? await OwnerMetadataPointsToSourceAsync(
                    trackedAudiobook,
                    trackedFile,
                    journal,
                    cancellationToken)
                : false;
            if (ownerPointsToSource == null)
            {
                logger.LogWarning(
                    "Completed organize journal {OperationId} remains pending because owner path identity is temporarily unavailable",
                    operationId);
                return;
            }
            if (ownerPointsToSource == true)
            {
                if (BeforeOwnerMetadataCommitForTestAsync != null)
                {
                    await BeforeOwnerMetadataCommitForTestAsync(operationId);
                }

                var compensationCommit = await CommitRecoveredOwnerMetadataAsync(
                    db,
                    journal,
                    journal.SourcePath,
                    journal.SourcePhysicalObjectIdentity,
                    cancellationToken);
                if (compensationCommit == GenerationMatchOutcome.Unavailable)
                {
                    logger.LogWarning(
                        "Completed organize journal {OperationId} remains pending because its compensation source generation became temporarily unavailable before owner-metadata reconciliation",
                        operationId);
                    return;
                }
                if (compensationCommit == GenerationMatchOutcome.Mismatch)
                {
                    await MarkNeedsAttentionAsync(
                        operationId,
                        "The compensation source generation changed before owner metadata could be reconciled.",
                        cancellationToken);
                    return;
                }

                logger.LogInformation(
                    "Reconciled compensated organize journal {OperationId}; the original source generation and owner metadata were already restored",
                    operationId);
                return;
            }

            await MarkNeedsAttentionAsync(
                operationId,
                "The completed organize destination no longer identifies the journaled physical file generation.",
                cancellationToken);
            return;
        }

        if (trackedFile != null
            && (string.IsNullOrWhiteSpace(trackedFile.PhysicalObjectIdentity)
                || !PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
                    trackedFile.PhysicalObjectIdentity,
                    journal.SourcePhysicalObjectIdentity)))
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The tracked audiobook file no longer identifies the source generation owned by the organize journal.",
                cancellationToken);
            return;
        }

        if (trackedFile == null)
        {
            trackedAudiobook.FilePath = journal.DestinationPath;
        }
        else
        {
            var destinationIdentity = await identityResolver.ResolveAsync(
                trackedAudiobook,
                journal.DestinationPath,
                cancellationToken);
            if (destinationIdentity.State == PathIdentityState.Unavailable)
            {
                logger.LogWarning(
                    "Completed organize journal {OperationId} remains pending because destination path identity is temporarily unavailable: {Reason}",
                    operationId,
                    destinationIdentity.Reason);
                return;
            }
            if (destinationIdentity.State != PathIdentityState.Valid)
            {
                await MarkNeedsAttentionAsync(
                    operationId,
                    "The completed organize destination no longer has a valid filesystem path identity.",
                    cancellationToken);
                return;
            }

            trackedFile.ApplyPathIdentity(journal.DestinationPath, destinationIdentity);
            trackedFile.ApplyPhysicalObjectIdentity(
                journal.TargetPhysicalObjectIdentity,
                timeProvider.GetUtcNow().UtcDateTime);
        }

        PathNormalizationOutcome normalization;
        try
        {
            normalization = await NormalizeAudiobookPathsAsync(
                trackedAudiobook,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await MarkNeedsAttentionAsync(
                operationId,
                $"The completed organize owner metadata cannot be normalized safely: {exception.Message}",
                cancellationToken);
            return;
        }
        if (normalization == PathNormalizationOutcome.Unavailable)
        {
            logger.LogWarning(
                "Completed organize journal {OperationId} remains pending because sibling path identity is temporarily unavailable",
                operationId);
            return;
        }
        if (normalization == PathNormalizationOutcome.Conflict)
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The completed organize owner metadata no longer has one coherent filesystem path identity.",
                cancellationToken);
            return;
        }

        if (BeforeOwnerMetadataCommitForTestAsync != null)
        {
            await BeforeOwnerMetadataCommitForTestAsync(operationId);
        }

        var ownerMetadataCommit = await CommitRecoveredOwnerMetadataAsync(
            db,
            journal,
            journal.DestinationPath,
            journal.TargetPhysicalObjectIdentity,
            cancellationToken);
        if (ownerMetadataCommit == GenerationMatchOutcome.Unavailable)
        {
            logger.LogWarning(
                "Completed organize journal {OperationId} remains pending because its destination generation became temporarily unavailable before owner-metadata reconciliation",
                operationId);
            return;
        }
        if (ownerMetadataCommit == GenerationMatchOutcome.Mismatch)
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The completed organize destination changed before owner metadata could be reconciled.",
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "Reconciled interrupted organize journal {OperationId} for audiobook {AudiobookId}",
            operationId,
            trackedAudiobook.Id);
    }

    private async Task MarkNeedsAttentionAsync(
        Guid operationId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var journal = await db.FileMutationJournals
            .SingleAsync(candidate => candidate.OperationId == operationId, cancellationToken);
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
