using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs;

[Trait("Area", "Audiobooks")]
[Trait("Name", "MoveRecoveryPolicyTests")]
[Trait("Category", "Application")]
public sealed class MoveRecoveryPolicyTests : BaseTests
{
    [Fact]
    public void ClassifyAudiobookJobs_FailedPublishedUnknown_IsRetryableAndBlocking()
    {
        var job = CreateJob(
            MoveJobStatus.Failed,
            MoveJobPhase.Published,
            MoveFailureKind.Unknown,
            MoveJobEntryCopyState.Verified,
            MoveJobEntryCleanupState.Deleted);

        var state = MoveRecoveryPolicy.ClassifyAudiobookJobs([job]);

        Assert.Equal(MoveRecoveryDisposition.RetryAvailable, state.Disposition);
        Assert.True(state.BlocksFilesystemMutation);
        Assert.True(state.CanRetry);
        Assert.Equal(job.Id, state.JobId);
    }

    [Fact]
    public void ClassifyAudiobookJobs_PreMutationHistoricalFailure_DoesNotBlock()
    {
        var job = CreateJob(
            MoveJobStatus.Failed,
            MoveJobPhase.Planned,
            MoveFailureKind.Unknown,
            MoveJobEntryCopyState.Pending,
            MoveJobEntryCleanupState.Pending);

        var state = MoveRecoveryPolicy.ClassifyAudiobookJobs([job]);

        Assert.Equal(MoveRecoveryDisposition.None, state.Disposition);
        Assert.False(state.BlocksFilesystemMutation);
        Assert.False(state.CanRetry);
    }

    [Fact]
    public void ClassifyAudiobookJobs_MarkerlessNeedsAttentionUnknownWithCompletedRecoveryEvidence_IsRetryable()
    {
        var job = CreateJob(
            MoveJobStatus.NeedsAttention,
            MoveJobPhase.Published,
            MoveFailureKind.Unknown,
            MoveJobEntryCopyState.Verified,
            MoveJobEntryCleanupState.Deleted);
        job.ExecutionProtocolVersion = MoveExecutionProtocol.MarkerlessDatabaseState;
        job.SourceDirectoryCleanupState = MoveJobEntryCleanupState.Deleted;
        job.TargetDirectoryObjectIdentity = "target-generation";
        var targetSemantics = FileSystemPathSemantics.CurrentHostDefault;
        job.SetTargetIdentity(new PathIdentitySnapshot(
            targetSemantics.Syntax,
            targetSemantics.CaseSensitivity,
            FileSystemCaseSensitivityMode.Auto,
            Path.GetPathRoot(job.RequestedPath!)!));
        job.CreatedDirectories =
        [
            new MoveJobCreatedDirectory
            {
                Path = job.RequestedPath!,
                State = MoveCreatedDirectoryState.Created,
                DirectoryObjectIdentity = job.TargetDirectoryObjectIdentity
            }
        ];

        var state = MoveRecoveryPolicy.ClassifyAudiobookJobs([job]);

        Assert.Equal(MoveRecoveryDisposition.RetryAvailable, state.Disposition);
        Assert.True(state.BlocksFilesystemMutation);
        Assert.True(state.CanRetry);
        Assert.Equal(job.Id, state.JobId);
    }

    [Fact]
    public void ClassifyAudiobookJobs_MarkerlessNeedsAttentionUnknownWithoutExactTargetGeneration_IsOperatorRepairOnly()
    {
        var job = CreateJob(
            MoveJobStatus.NeedsAttention,
            MoveJobPhase.Published,
            MoveFailureKind.Unknown,
            MoveJobEntryCopyState.Verified,
            MoveJobEntryCleanupState.Deleted);
        job.ExecutionProtocolVersion = MoveExecutionProtocol.MarkerlessDatabaseState;
        job.SourceDirectoryCleanupState = MoveJobEntryCleanupState.Deleted;
        job.TargetDirectoryObjectIdentity = "expected-generation";
        var targetSemantics = FileSystemPathSemantics.CurrentHostDefault;
        job.SetTargetIdentity(new PathIdentitySnapshot(
            targetSemantics.Syntax,
            targetSemantics.CaseSensitivity,
            FileSystemCaseSensitivityMode.Auto,
            Path.GetPathRoot(job.RequestedPath!)!));
        job.CreatedDirectories =
        [
            new MoveJobCreatedDirectory
            {
                Path = job.RequestedPath!,
                State = MoveCreatedDirectoryState.Created,
                DirectoryObjectIdentity = "different-generation"
            }
        ];

        var state = MoveRecoveryPolicy.ClassifyAudiobookJobs([job]);

        Assert.Equal(MoveRecoveryDisposition.OperatorRepairRequired, state.Disposition);
        Assert.True(state.BlocksFilesystemMutation);
        Assert.False(state.CanRetry);
    }

    [Fact]
    public void ClassifyAudiobookJobs_NeedsAttentionVerification_IsOperatorRepairOnly()
    {
        var job = CreateJob(
            MoveJobStatus.NeedsAttention,
            MoveJobPhase.CleaningSource,
            MoveFailureKind.Verification,
            MoveJobEntryCopyState.Verified,
            MoveJobEntryCleanupState.DeleteAuthorized);

        var state = MoveRecoveryPolicy.ClassifyAudiobookJobs([job]);

        Assert.Equal(MoveRecoveryDisposition.OperatorRepairRequired, state.Disposition);
        Assert.True(state.BlocksFilesystemMutation);
        Assert.False(state.CanRetry);
    }

    [Theory]
    [InlineData(MoveJobStatus.NeedsAttention)]
    [InlineData(MoveJobStatus.Failed)]
    [InlineData(MoveJobStatus.Queued)]
    public void ClassifyAudiobookJobs_PreDurableUnresolvedJob_BlocksWithoutCurrentExecutionEvidence(
        MoveJobStatus status)
    {
        var job = CreateJob(
            status,
            MoveJobPhase.None,
            MoveFailureKind.Verification,
            MoveJobEntryCopyState.Pending,
            MoveJobEntryCleanupState.Pending);
        job.ExecutionProtocolVersion = MoveExecutionProtocol.PreDurableReleased;
        job.Entries.Clear();

        var state = MoveRecoveryPolicy.ClassifyAudiobookJobs([job]);

        Assert.Equal(MoveRecoveryDisposition.OperatorRepairRequired, state.Disposition);
        Assert.True(state.BlocksFilesystemMutation);
        Assert.False(state.CanRetry);
        Assert.Equal(job.Id, state.JobId);
    }

    [Fact]
    public void ClassifyAudiobookJobs_PreDurableCompletedJob_DoesNotBlock()
    {
        var job = CreateJob(
            MoveJobStatus.Completed,
            MoveJobPhase.None,
            MoveFailureKind.None,
            MoveJobEntryCopyState.Pending,
            MoveJobEntryCleanupState.Pending);
        job.ExecutionProtocolVersion = MoveExecutionProtocol.PreDurableReleased;
        job.Entries.Clear();

        var state = MoveRecoveryPolicy.ClassifyAudiobookJobs([job]);

        Assert.Equal(MoveRecoveryDisposition.None, state.Disposition);
        Assert.False(state.BlocksFilesystemMutation);
    }

    [Fact]
    public void ClassifyAudiobookJobs_MultipleUnresolvedExecutions_FailsClosedAsAmbiguous()
    {
        var first = CreateJob(
            MoveJobStatus.Failed,
            MoveJobPhase.Published,
            MoveFailureKind.Unknown,
            MoveJobEntryCopyState.Verified,
            MoveJobEntryCleanupState.Deleted);
        var second = CreateJob(
            MoveJobStatus.NeedsAttention,
            MoveJobPhase.CleaningSource,
            MoveFailureKind.Transient,
            MoveJobEntryCopyState.Verified,
            MoveJobEntryCleanupState.DeleteAuthorized);

        var state = MoveRecoveryPolicy.ClassifyAudiobookJobs([first, second]);

        Assert.Equal(MoveRecoveryDisposition.Ambiguous, state.Disposition);
        Assert.True(state.BlocksFilesystemMutation);
        Assert.False(state.CanRetry);
        Assert.Null(state.JobId);
        Assert.Equal(2, state.BlockingJobIds.Count);
    }

    private static MoveJob CreateJob(
        MoveJobStatus status,
        MoveJobPhase phase,
        MoveFailureKind failureKind,
        MoveJobEntryCopyState copyState,
        MoveJobEntryCleanupState cleanupState) =>
        new()
        {
            Id = Guid.NewGuid(),
            AudiobookId = 42,
            SourcePath = Path.GetFullPath(Path.Join("source", Guid.NewGuid().ToString("N"))),
            RequestedPath = Path.GetFullPath(Path.Join("target", Guid.NewGuid().ToString("N"))),
            Status = status,
            Phase = phase,
            FailureKind = failureKind,
            Entries =
            [
                new MoveJobEntry
                {
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = 1,
                    LastWriteTimeUtc = DateTime.UtcNow,
                    Sha256 = new string('A', 64),
                    CopyState = copyState,
                    CleanupState = cleanupState
                }
            ]
        };
}
