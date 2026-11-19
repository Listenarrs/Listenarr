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
        var operationIds = await readContext.FileMutationJournals
            .AsNoTracking()
            .Where(journal =>
                journal.Action == FileAction.Move
                && journal.AudiobookId != null
                && journal.AudiobookFileId != null
                && (journal.AudiobookFileId == FileMutationOwner.CompanionFile
                    || journal.AudiobookFileId
                        == FileMutationOwner.RegistrationCompanionFile
                    ? journal.State != FileMutationJournalState.Completed
                        && journal.State
                            != FileMutationJournalState.CompletedSourceRetained
                        && journal.State != FileMutationJournalState.RolledBack
                        && journal.State != FileMutationJournalState.NeedsAttention
                    : journal.State != FileMutationJournalState.OwnerMetadataReconciled
                        && journal.State != FileMutationJournalState.RolledBack
                        && journal.State != FileMutationJournalState.NeedsAttention))
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
        Audiobook? audiobook;
        AudiobookFile? audiobookFile;
        int ownerAudiobookId;
        int ownerAudiobookFileId;
        bool isCompanionFile;
        await using (var context = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            journal = await context.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId, cancellationToken);
            if (journal.State is FileMutationJournalState.OwnerMetadataReconciled
                or FileMutationJournalState.NeedsAttention
                or FileMutationJournalState.RolledBack)
            {
                return;
            }

            if (!journal.AudiobookId.HasValue || !journal.AudiobookFileId.HasValue)
            {
                return;
            }

            ownerAudiobookId = journal.AudiobookId.Value;
            ownerAudiobookFileId = journal.AudiobookFileId.Value;
            isCompanionFile = FileMutationOwner.IsCompanionFile(
                ownerAudiobookFileId);
            audiobook = await context.Audiobooks
                .AsNoTracking()
                .Include(candidate => candidate.Files)
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == journal.AudiobookId.Value,
                    cancellationToken);
            if (audiobook == null)
            {
                await MarkNeedsAttentionAsync(
                    operationId,
                    "The interrupted owner-bound publication references an audiobook that no longer exists. Filesystem artifacts were preserved.",
                    cancellationToken);
                return;
            }

            audiobookFile = ownerAudiobookFileId == 0 || isCompanionFile
                ? null
                : audiobook.Files?.SingleOrDefault(
                    file => file.Id == ownerAudiobookFileId);
            if (!isCompanionFile
                && ownerAudiobookFileId != 0
                && audiobookFile == null)
            {
                await MarkNeedsAttentionAsync(
                    operationId,
                    "The interrupted owner-bound publication references an audiobook file row that no longer exists. Filesystem artifacts were preserved.",
                    cancellationToken);
                return;
            }
        }

        if (AfterInitialOwnerBindingLoadedForTestAsync != null)
        {
            await AfterInitialOwnerBindingLoadedForTestAsync(operationId);
        }

        if (journal.State < FileMutationJournalState.Completed)
        {
            var precommitTargetContent = await ProbeTargetContentAsync(
                journal,
                cancellationToken);
            if (precommitTargetContent == GenerationMatchOutcome.Unavailable)
            {
                logger.LogWarning(
                    "Owner-bound file recovery {OperationId} remains pending because its destination content is temporarily unavailable",
                    operationId);
                return;
            }

            var sourceContent = await ProbeSourceContentAsync(
                journal,
                cancellationToken);
            if (precommitTargetContent == GenerationMatchOutcome.Match)
            {
                var completedState =
                    sourceContent == GenerationMatchOutcome.Missing
                        ? FileMutationJournalState.Completed
                        : FileMutationJournalState.CompletedSourceRetained;
                await SetRecoveryStateAsync(
                    operationId,
                    completedState,
                    completedState
                        == FileMutationJournalState.CompletedSourceRetained
                        ? "The published target was recovered after restart; any surviving source was retained because restart cannot recreate deletion authority."
                        : null,
                    cancellationToken);
                journal.State = completedState;
                if (isCompanionFile)
                {
                    logger.LogInformation(
                        "Recovered interrupted companion publication {OperationId} without replaying source cleanup",
                        operationId);
                    return;
                }
            }
            else if (precommitTargetContent == GenerationMatchOutcome.Missing
                && sourceContent == GenerationMatchOutcome.Match)
            {
                await SetRecoveryStateAsync(
                    operationId,
                    FileMutationJournalState.RolledBack,
                    "The destination was never durably published; the original source remained authoritative.",
                    cancellationToken);
                return;
            }
            else if (precommitTargetContent == GenerationMatchOutcome.Missing
                && sourceContent == GenerationMatchOutcome.Unavailable)
            {
                logger.LogWarning(
                    "Owner-bound file recovery {OperationId} remains pending because its source content is temporarily unavailable",
                    operationId);
                return;
            }
            else
            {
                await MarkNeedsAttentionAsync(
                    operationId,
                    "The interrupted owner-bound publication is ambiguous. Source and destination artifacts were preserved and no restart mutation was authorized.",
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
        if (journal.State is not (
                FileMutationJournalState.Completed
                or FileMutationJournalState.CompletedSourceRetained)
            || string.IsNullOrWhiteSpace(journal.SourceSha256))
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The interrupted organize journal has no verified operation content proof.",
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
        if (isCompanionFile)
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
                cancellationToken);
        if (trackedAudiobook == null)
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The completed organize publication references an audiobook that no longer exists. Filesystem artifacts were preserved.",
                cancellationToken);
            return;
        }

        var trackedFile = ownerAudiobookFileId == 0
            ? null
            : trackedAudiobook.Files?.SingleOrDefault(
                file => file.Id == ownerAudiobookFileId);
        if (ownerAudiobookFileId != 0 && trackedFile == null)
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The completed organize publication references an audiobook file row that no longer exists. Filesystem artifacts were preserved.",
                cancellationToken);
            return;
        }

        var targetContent = await ProbeTargetContentAsync(
            journal,
            cancellationToken);
        if (targetContent == GenerationMatchOutcome.Unavailable)
        {
            logger.LogWarning(
                "Completed organize journal {OperationId} remains pending because its destination content is temporarily unavailable",
                operationId);
            return;
        }
        if (targetContent != GenerationMatchOutcome.Match)
        {
            var compensationSource = await ProbeSourceContentAsync(
                journal,
                cancellationToken);
            if (compensationSource == GenerationMatchOutcome.Unavailable)
            {
                logger.LogWarning(
                    "Completed organize journal {OperationId} remains pending because its compensation source content is temporarily unavailable",
                    operationId);
                return;
            }

            if (compensationSource == GenerationMatchOutcome.Match)
            {
                var ownerAtSource = await OwnerMetadataPointsToPathAsync(
                    trackedAudiobook,
                    trackedFile,
                    journal.SourcePath,
                    cancellationToken);
                if (ownerAtSource == null)
                {
                    logger.LogWarning(
                        "Completed organize journal {OperationId} remains pending because compensation owner path identity is temporarily unavailable",
                        operationId);
                    return;
                }

                if (ownerAtSource == true)
                {
                    if (BeforeOwnerMetadataCommitForTestAsync != null)
                    {
                        await BeforeOwnerMetadataCommitForTestAsync(operationId);
                    }

                    var compensationCommit =
                        await CommitRecoveredOwnerMetadataAsync(
                            db,
                            journal,
                            journal.SourcePath,
                            cancellationToken);
                    if (compensationCommit
                        == GenerationMatchOutcome.Unavailable)
                    {
                        logger.LogWarning(
                            "Completed organize journal {OperationId} remains pending because its compensation source changed availability before reconciliation",
                            operationId);
                        return;
                    }

                    if (compensationCommit
                        != GenerationMatchOutcome.Match)
                    {
                        await MarkNeedsAttentionAsync(
                            operationId,
                            "The compensation source content changed before owner metadata could be reconciled.",
                            cancellationToken);
                        return;
                    }

                    logger.LogInformation(
                        "Reconciled compensated organize journal {OperationId}; owner metadata already points to the retained source",
                        operationId);
                    return;
                }
            }

            await MarkNeedsAttentionAsync(
                operationId,
                "The completed organize destination no longer matches the journaled content proof and no authoritative compensation source could be established.",
                cancellationToken);
            return;
        }

        var ownerPointsToSource = await OwnerMetadataPointsToPathAsync(
            trackedAudiobook,
            trackedFile,
            journal.SourcePath,
            cancellationToken);
        if (ownerPointsToSource == null)
        {
            logger.LogWarning(
                "Completed organize journal {OperationId} remains pending because owner path identity is temporarily unavailable",
                operationId);
            return;
        }
        if (ownerPointsToSource == false)
        {
            var ownerPointsToDestination = await OwnerMetadataPointsToPathAsync(
                trackedAudiobook,
                trackedFile,
                journal.DestinationPath,
                cancellationToken);
            if (ownerPointsToDestination == null)
            {
                logger.LogWarning(
                    "Completed organize journal {OperationId} remains pending because destination owner path identity is temporarily unavailable",
                    operationId);
                return;
            }
            if (ownerPointsToDestination == false)
            {
                await MarkNeedsAttentionAsync(
                    operationId,
                    "The completed organize owner metadata points to neither the source nor destination path.",
                    cancellationToken);
                return;
            }
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
            trackedFile.ClearPhysicalObjectIdentity();
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
            cancellationToken);
        if (ownerMetadataCommit == GenerationMatchOutcome.Unavailable)
        {
            logger.LogWarning(
                "Completed organize journal {OperationId} remains pending because its destination content became temporarily unavailable before owner-metadata reconciliation",
                operationId);
            return;
        }
        if (ownerMetadataCommit != GenerationMatchOutcome.Match)
        {
            await MarkNeedsAttentionAsync(
                operationId,
                "The completed organize destination content or path changed before owner metadata could be reconciled.",
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "Reconciled interrupted organize journal {OperationId} for audiobook {AudiobookId}",
            operationId,
            trackedAudiobook.Id);
    }

}
