using Listenarr.Application.Common;
using Listenarr.Application.Common.Exceptions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    public async Task<MoveRecoveryState> GetRecoveryStateForAudiobookAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MoveJob> jobs;
        try
        {
            jobs = await _persistence.GetRecoveryCandidatesByAudiobookAsync(
                audiobookId,
                cancellationToken);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            _logger.LogWarning(
                exception,
                "Failed to query unresolved move state for audiobook {AudiobookId}",
                audiobookId);
            throw;
        }

        return MoveRecoveryPolicy.ClassifyAudiobookJobs(jobs);
    }

    public async Task<IReadOnlyList<MoveJob>> GetFilesystemBlockingJobsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MoveJob> jobs;
        try
        {
            jobs = await _persistence.GetRecoveryCandidatesAsync(cancellationToken);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            _logger.LogWarning(exception, "Failed to query filesystem-blocking move jobs");
            throw;
        }

        return jobs
            .Where(MoveRecoveryPolicy.BlocksFilesystemMutation)
            .ToArray();
    }

    public async Task EnsureFilesystemMutationAllowedAsync(
        int audiobookId,
        CancellationToken cancellationToken = default,
        bool allowActiveDeletionIntent = false)
    {
        await EnsureExternalRecoveryAllowsMutationAsync(
            audiobookId,
            allowActiveDeletionIntent,
            cancellationToken);

        var recovery = await GetRecoveryStateForAudiobookAsync(
            audiobookId,
            cancellationToken);
        if (!recovery.BlocksFilesystemMutation)
        {
            return;
        }

        throw recovery.Disposition switch
        {
            MoveRecoveryDisposition.InProgress => new ApplicationConflictException(
                "move_already_active",
                "A move is already in progress for this audiobook. Wait for it to finish before changing its files."),
            MoveRecoveryDisposition.RetryAvailable => new ApplicationConflictException(
                "move_recovery_required",
                "An interrupted move still owns this audiobook's filesystem state. Resume that move before changing its files."),
            MoveRecoveryDisposition.OperatorRepairRequired => new ApplicationConflictException(
                "move_repair_required",
                "A previous move left unresolved filesystem state that requires repair before this audiobook can be changed."),
            MoveRecoveryDisposition.Ambiguous => new ApplicationConflictException(
                "move_recovery_ambiguous",
                "Multiple move jobs contain unresolved filesystem state for this audiobook. Operator reconciliation is required before changing its files."),
            _ => new ApplicationConflictException(
                "move_recovery_required",
                "An unresolved move must be completed before changing this audiobook's files.")
        };
    }

    private async Task EnsureExternalRecoveryAllowsMutationAsync(
        int audiobookId,
        bool allowActiveDeletionIntent,
        CancellationToken cancellationToken)
    {
        if (await _relocationService.IsAudiobookPathStateProtectedAsync(
                audiobookId,
                cancellationToken))
        {
            throw new ApplicationConflictException(
                "root_folder_relocation_active",
                "An active root-folder path repair still owns this audiobook's path state. Resolve or retry that repair before changing the audiobook's files.");
        }

        if (_fileRenameRecoveryProbe != null
            && await _fileRenameRecoveryProbe.HasBlockingAsync(
                audiobookId,
                cancellationToken))
        {
            throw new ApplicationConflictException(
                "rename_recovery_pending",
                "An interrupted file organize operation still owns this audiobook's filesystem state. Restart recovery must reconcile it before changing the audiobook's files.");
        }

        if (!allowActiveDeletionIntent
            && _deletionIntentProbe != null
            && await _deletionIntentProbe.HasActiveAsync(
                audiobookId,
                cancellationToken))
        {
            throw new ApplicationConflictException(
                "delete_recovery_pending",
                "An audiobook deletion still owns this audiobook's filesystem state. Complete or retry that deletion before changing its files.");
        }
    }
}
