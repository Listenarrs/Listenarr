using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    private static MoveEnqueueCommand NormalizeSourceCleanupAuthorization(
        MoveEnqueueCommand command) =>
        command.ForceCopyAndRetainSource && command.DeleteEmptySource
            ? command with { DeleteEmptySource = false }
            : command;

    private static void ValidateSourceCleanupAuthorization(
        MoveEnqueueCommand command)
    {
        if (!Enum.IsDefined(command.SourceCleanupMode))
        {
            throw new ArgumentException(
                "The source cleanup mode is invalid.",
                nameof(command));
        }
        if (command.ForceCopyAndRetainSource
            && command.SourceCleanupMode != MoveSourceCleanupMode.RetainSource)
        {
            throw new ArgumentException(
                "Forced copy retention cannot be combined with source deletion authorization.",
                nameof(command));
        }
        if (command.SourceCleanupMode != MoveSourceCleanupMode.DeleteAfterVerifiedCopy)
        {
            return;
        }
        if (command.TargetRootFolderId is not > 0
            || command.TargetPolicyRevision is not >= 0
            || command.TargetStorageContractRevision is not >= 0
            || command.SourceRootFolderId.HasValue
                != command.SourcePolicyRevision.HasValue
            || command.SourceRootFolderId.HasValue
                != command.SourceStorageContractRevision.HasValue
            || command.SourceRootFolderId is <= 0
            || command.SourcePolicyRevision is < 0
            || command.SourceStorageContractRevision is < 0)
        {
            throw new ArgumentException(
                "Verified source deletion requires a complete root-folder policy snapshot.",
                nameof(command));
        }
    }

    private static void EnsureMatchingActiveExecutionOptions(
        MoveJob existingJob,
        MoveEnqueueCommand command,
        string? persistedSourceBoundary)
    {
        if (ActiveExecutionOptionsMatch(
                existingJob,
                command.SourceIdentity,
                command.TargetIdentity,
                command.DeleteEmptySource,
                command.RelocationId,
                persistedSourceBoundary,
                command.SourceCleanupMode,
                command.SourceRootFolderId,
                command.SourcePolicyRevision,
                command.TargetRootFolderId,
                command.TargetPolicyRevision,
                command.SourceStorageContractRevision,
                command.TargetStorageContractRevision,
                command.ForceCopyAndRetainSource))
        {
            return;
        }

        throw new ApplicationConflictException(
            "move_active_options_conflict",
            "An active move already owns this source and destination with different execution options. Wait for it to finish or resolve its recovery state before retrying.");
    }

    private static bool ActiveExecutionOptionsMatch(
        MoveJob existingJob,
        PathIdentitySnapshot sourceIdentity,
        PathIdentitySnapshot targetIdentity,
        bool deleteEmptySource,
        Guid? relocationId,
        string? persistedSourceBoundary,
        MoveSourceCleanupMode sourceCleanupMode,
        int? sourceRootFolderId,
        int? sourcePolicyRevision,
        int? targetRootFolderId,
        int? targetPolicyRevision,
        int? sourceStorageContractRevision,
        int? targetStorageContractRevision,
        bool forceCopyAndRetainSource) =>
        MoveExecutionContract.Matches(
            existingJob,
            sourceIdentity,
            targetIdentity,
            deleteEmptySource,
            persistedSourceBoundary,
            relocationId,
            sourceCleanupMode,
            sourceRootFolderId,
            sourcePolicyRevision,
            targetRootFolderId,
            targetPolicyRevision,
            sourceStorageContractRevision,
            targetStorageContractRevision,
            forceCopyAndRetainSource);
}
