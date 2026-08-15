namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record MoveJobPublicUpdate(
    string JobId,
    int? AudiobookId,
    string Status,
    string? Error,
    string? Target,
    double Progress,
    string Phase,
    DateTime UpdatedAt);

public static class MoveJobPublicProjection
{
    public static string? ToError(MoveJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ToError(job.Error, job.FailureKind);
    }

    public static string? ToError(
        string? internalError,
        MoveFailureKind failureKind)
    {
        if (string.IsNullOrWhiteSpace(internalError))
        {
            return null;
        }

        return failureKind switch
        {
            MoveFailureKind.Transient =>
                "The move was interrupted and can be retried.",
            MoveFailureKind.SourceDrift =>
                "The source changed during the move. Review the source and retry.",
            MoveFailureKind.Verification =>
                "The moved files could not be verified.",
            MoveFailureKind.UnsupportedEntry =>
                "The source contains an unsupported filesystem entry.",
            MoveFailureKind.Persistence =>
                "The move state could not be saved.",
            MoveFailureKind.Unknown or MoveFailureKind.None =>
                "The move failed. Review the server logs for details.",
            _ => "The move failed. Review the server logs for details."
        };
    }

    public static MoveJobPublicUpdate CreateUpdate(
        Guid jobId,
        MoveJobStatus fallbackStatus,
        string? fallbackError,
        DateTime fallbackUpdatedAt,
        MoveJob? persistedJob,
        double? progressOverride = null,
        string? phaseOverride = null)
    {
        var status = persistedJob?.Status ?? fallbackStatus;
        var error = persistedJob == null
            ? ToError(fallbackError, MoveFailureKind.Unknown)
            : ToError(persistedJob);
        return new MoveJobPublicUpdate(
            jobId.ToString(),
            persistedJob?.AudiobookId,
            status.ToString(),
            error,
            persistedJob?.RequestedPath,
            Math.Clamp(
                progressOverride ?? CalculateProgress(persistedJob, status),
                0,
                100),
            phaseOverride ?? persistedJob?.Phase.ToString() ?? MoveJobPhase.None.ToString(),
            persistedJob?.UpdatedAt ?? fallbackUpdatedAt);
    }

    public static double CalculateProgress(MoveJob? job, MoveJobStatus fallbackStatus)
    {
        var status = job?.Status ?? fallbackStatus;
        if (status == MoveJobStatus.Completed)
        {
            return 100;
        }
        if (status == MoveJobStatus.Queued)
        {
            return 0;
        }

        var phase = job?.Phase ?? MoveJobPhase.None;
        var files = job?.Entries
            .Where(entry => entry.EntryType == MoveJobEntryType.File
                && !MoveManifestIdentity.IsTargetBoundaryAuthorization(entry))
            .ToList() ?? [];
        var totalBytes = files.Sum(entry => Math.Max(entry.Length, 1));
        var copiedBytes = files
            .Where(entry => entry.CopyState == MoveJobEntryCopyState.Verified)
            .Sum(entry => Math.Max(entry.Length, 1));
        var cleanedBytes = files
            .Where(entry => entry.CleanupState is
                MoveJobEntryCleanupState.Deleted or MoveJobEntryCleanupState.Retained)
            .Sum(entry => Math.Max(entry.Length, 1));
        var copyRatio = totalBytes == 0 ? 0 : (double)copiedBytes / totalBytes;
        var cleanupRatio = totalBytes == 0 ? 0 : (double)cleanedBytes / totalBytes;

        return phase switch
        {
            MoveJobPhase.None => 1,
            MoveJobPhase.Planned => 5,
            MoveJobPhase.Copying => 5 + (copyRatio * 65),
            MoveJobPhase.Published => 72,
            MoveJobPhase.CleaningSource => 75 + (cleanupRatio * 15),
            MoveJobPhase.Finalizing => 92,
            MoveJobPhase.CleaningArtifacts => 98,
            MoveJobPhase.RecordingCompletion => 99,
            _ => 1
        };
    }
}
