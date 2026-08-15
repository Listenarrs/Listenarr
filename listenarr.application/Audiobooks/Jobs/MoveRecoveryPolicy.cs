namespace Listenarr.Application.Audiobooks.Jobs;

public enum MoveRecoveryDisposition
{
    None,
    InProgress,
    RetryAvailable,
    OperatorRepairRequired,
    Ambiguous
}

public sealed record MoveRecoveryState(
    MoveRecoveryDisposition Disposition,
    Guid? JobId,
    MoveJobStatus? Status,
    MoveJobPhase? Phase,
    string? RequestedPath,
    string? Error,
    IReadOnlyList<Guid> BlockingJobIds)
{
    public bool BlocksFilesystemMutation => Disposition is
        MoveRecoveryDisposition.InProgress or
        MoveRecoveryDisposition.RetryAvailable or
        MoveRecoveryDisposition.OperatorRepairRequired or
        MoveRecoveryDisposition.Ambiguous;

    public bool CanRetry => Disposition == MoveRecoveryDisposition.RetryAvailable;

    public static MoveRecoveryState None { get; } = new(
        MoveRecoveryDisposition.None,
        JobId: null,
        Status: null,
        Phase: null,
        RequestedPath: null,
        Error: null,
        BlockingJobIds: []);
}

public static class MoveRecoveryPolicy
{
    public static bool HasFilesystemExecutionEvidence(MoveJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.Phase >= MoveJobPhase.Copying)
        {
            return true;
        }

        if (job.Entries.Any(entry =>
                entry.EntryType == MoveJobEntryType.File
                && (entry.CopyState != MoveJobEntryCopyState.Pending
                    || entry.CleanupState != MoveJobEntryCleanupState.Pending)))
        {
            return true;
        }

        return job.CreatedDirectories.Any(directory => directory.State is
            MoveCreatedDirectoryState.Created or
            MoveCreatedDirectoryState.Retained);
    }

    public static bool BlocksFilesystemMutation(MoveJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Status is MoveJobStatus.Completed or MoveJobStatus.Superseded)
        {
            return false;
        }

        if (!MoveExecutionProtocol.IsCurrent(job.ExecutionProtocolVersion))
        {
            // Released pre-durable jobs and unsupported development protocols cannot
            // carry trustworthy manifest/generation evidence. Their absence of current
            // evidence is therefore not proof that no filesystem mutation occurred.
            return true;
        }

        if (job.Status.IsActive())
        {
            return true;
        }

        return job.Status is MoveJobStatus.Failed or MoveJobStatus.NeedsAttention
            && HasFilesystemExecutionEvidence(job);
    }

    public static MoveRecoveryDisposition GetDisposition(MoveJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Status is MoveJobStatus.Completed or MoveJobStatus.Superseded)
        {
            return MoveRecoveryDisposition.None;
        }

        if (!MoveExecutionProtocol.IsCurrent(job.ExecutionProtocolVersion))
        {
            return MoveRecoveryDisposition.OperatorRepairRequired;
        }

        if (job.Status.IsActive())
        {
            return MoveRecoveryDisposition.InProgress;
        }

        if (job.Status == MoveJobStatus.Failed)
        {
            // Failed is the legacy/manual-retry terminal state. Requeue never trusts the
            // failure classification by itself: it first requires persisted endpoint,
            // manifest, and target-boundary authorization evidence, and the worker then
            // re-verifies the exact recovery artifacts before any mutation. NeedsAttention
            // remains the state used to fence conditions that are known to require repair.
            return MoveRecoveryDisposition.RetryAvailable;
        }

        if (job.Status == MoveJobStatus.NeedsAttention)
        {
            if (job.FailureKind is MoveFailureKind.Transient or MoveFailureKind.Persistence)
            {
                return MoveRecoveryDisposition.RetryAvailable;
            }

            if (job.FailureKind == MoveFailureKind.Unknown
                && HasCompletedMarkerlessRecoveryEvidence(job))
            {
                return MoveRecoveryDisposition.RetryAvailable;
            }

            return MoveRecoveryDisposition.OperatorRepairRequired;
        }

        return MoveRecoveryDisposition.None;
    }

    private static bool HasCompletedMarkerlessRecoveryEvidence(MoveJob job)
    {
        if (!MoveExecutionProtocol.IsCurrent(job.ExecutionProtocolVersion)
            || job.SourceDirectoryCleanupState != MoveJobEntryCleanupState.Deleted
            || string.IsNullOrWhiteSpace(job.TargetDirectoryObjectIdentity)
            || string.IsNullOrWhiteSpace(job.RequestedPath))
        {
            return false;
        }

        var fileEntries = job.Entries
            .Where(entry => entry.EntryType == MoveJobEntryType.File)
            .ToList();
        if (fileEntries.Count == 0
            || fileEntries.Any(entry =>
                entry.CopyState != MoveJobEntryCopyState.Verified
                || entry.CleanupState != MoveJobEntryCleanupState.Deleted))
        {
            return false;
        }

        if (!job.TryGetTargetIdentity(out var targetIdentity)
            || !Listenarr.Domain.Common.FileSystemPathIdentity
                .TryCanonicalizeStoredPathWithIdentityForHost(
                    job.RequestedPath,
                    targetIdentity,
                    out var requestedPath,
                    out _))
        {
            return false;
        }

        foreach (var directory in job.CreatedDirectories)
        {
            if (directory.State != MoveCreatedDirectoryState.Created
                || string.IsNullOrWhiteSpace(directory.DirectoryObjectIdentity)
                || !string.Equals(
                    directory.DirectoryObjectIdentity,
                    job.TargetDirectoryObjectIdentity,
                    StringComparison.Ordinal)
                || !Listenarr.Domain.Common.FileSystemPathIdentity
                    .TryCanonicalizeStoredPathWithIdentityForHost(
                        directory.Path,
                        targetIdentity,
                        out var directoryPath,
                        out _))
            {
                continue;
            }

            if (Listenarr.Domain.Common.FileSystemPathIdentity.AreEquivalent(
                    directoryPath,
                    requestedPath,
                    targetIdentity.Semantics))
            {
                return true;
            }
        }

        return false;
    }

    public static MoveRecoveryState ClassifyAudiobookJobs(IEnumerable<MoveJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var blocking = jobs
            .Where(BlocksFilesystemMutation)
            .OrderBy(job => job.EnqueuedAt)
            .ThenBy(job => job.Id)
            .ToList();
        if (blocking.Count == 0)
        {
            return MoveRecoveryState.None;
        }

        if (blocking.Count > 1)
        {
            return new MoveRecoveryState(
                MoveRecoveryDisposition.Ambiguous,
                JobId: null,
                Status: null,
                Phase: null,
                RequestedPath: null,
                Error: "Multiple move jobs contain unresolved filesystem execution evidence.",
                BlockingJobIds: blocking.Select(job => job.Id).ToArray());
        }

        var job = blocking[0];
        return new MoveRecoveryState(
            GetDisposition(job),
            job.Id,
            job.Status,
            job.Phase,
            job.RequestedPath,
            job.Error,
            [job.Id]);
    }
}
