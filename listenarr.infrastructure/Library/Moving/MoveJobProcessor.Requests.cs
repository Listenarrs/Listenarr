using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private static AudiobookContentMoveRequest CreateContentMoveRequest(
        MoveJob job,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        string? cleanupBoundary,
        IReadOnlyDictionary<string, string>? sourcePhysicalObjectIdentities = null,
        Func<double, string, CancellationToken, Task>? progressReporter = null) =>
        new(
            source,
            target,
            job.Id,
            job.DeleteEmptySource && !job.ForceCopyAndRetainSource,
            sourceSemantics,
            targetSemantics,
            CreateLeaseToken(job),
            cleanupBoundary,
            SourcePhysicalObjectIdentities: sourcePhysicalObjectIdentities,
            ProgressReporter: progressReporter,
            SourceCleanupMode: job.SourceCleanupMode,
            SourceRootFolderId: job.SourceRootFolderId,
            SourcePolicyRevision: job.SourcePolicyRevision,
            TargetRootFolderId: job.TargetRootFolderId,
            TargetPolicyRevision: job.TargetPolicyRevision,
            SourceStorageContractRevision: job.SourceStorageContractRevision,
            TargetStorageContractRevision: job.TargetStorageContractRevision,
            ForceCopyAndRetainSource: job.ForceCopyAndRetainSource);
}
