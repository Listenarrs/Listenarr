using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public static class MoveExecutionContract
{
    public static bool Matches(
        MoveJob existingJob,
        PathIdentitySnapshot sourceIdentity,
        PathIdentitySnapshot targetIdentity,
        bool deleteEmptySource,
        string? sourceCleanupBoundary,
        Guid? relocationId,
        MoveSourceCleanupMode sourceCleanupMode = MoveSourceCleanupMode.RetainSource,
        int? sourceRootFolderId = null,
        int? sourcePolicyRevision = null,
        int? targetRootFolderId = null,
        int? targetPolicyRevision = null,
        int? sourceStorageContractRevision = null,
        int? targetStorageContractRevision = null,
        bool forceCopyAndRetainSource = false)
    {
        ArgumentNullException.ThrowIfNull(existingJob);

        if (string.IsNullOrWhiteSpace(existingJob.SourcePath)
            || string.IsNullOrWhiteSpace(existingJob.RequestedPath)
            || !existingJob.TryGetSourceIdentity(out var existingSourceIdentity)
            || !existingJob.TryGetTargetIdentity(out var existingTargetIdentity))
        {
            return false;
        }

        try
        {
            existingSourceIdentity.ValidateForPath(existingJob.SourcePath);
            existingTargetIdentity.ValidateForPath(existingJob.RequestedPath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }

        return IdentityMatches(existingSourceIdentity, sourceIdentity)
            && IdentityMatches(existingTargetIdentity, targetIdentity)
            && existingJob.DeleteEmptySource == deleteEmptySource
            && existingJob.SourceCleanupMode == sourceCleanupMode
            && existingJob.SourceRootFolderId == sourceRootFolderId
            && existingJob.SourcePolicyRevision == sourcePolicyRevision
            && existingJob.TargetRootFolderId == targetRootFolderId
            && existingJob.TargetPolicyRevision == targetPolicyRevision
            && existingJob.SourceStorageContractRevision == sourceStorageContractRevision
            && existingJob.TargetStorageContractRevision == targetStorageContractRevision
            && existingJob.ForceCopyAndRetainSource == forceCopyAndRetainSource
            && existingJob.RelocationId == relocationId
            && OptionalBoundaryMatches(
                existingJob.SourceCleanupBoundary,
                sourceCleanupBoundary,
                sourceIdentity.Semantics);
    }

    public static bool Matches(MoveJob left, MoveJob right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (string.IsNullOrWhiteSpace(left.SourcePath)
            || string.IsNullOrWhiteSpace(left.RequestedPath)
            || !left.TryGetSourceIdentity(out var leftSourceIdentity)
            || !left.TryGetTargetIdentity(out var leftTargetIdentity))
        {
            return false;
        }

        try
        {
            leftSourceIdentity.ValidateForPath(left.SourcePath);
            leftTargetIdentity.ValidateForPath(left.RequestedPath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }

        return Matches(
            right,
            leftSourceIdentity,
            leftTargetIdentity,
            left.DeleteEmptySource,
            left.SourceCleanupBoundary,
            left.RelocationId,
            left.SourceCleanupMode,
            left.SourceRootFolderId,
            left.SourcePolicyRevision,
            left.TargetRootFolderId,
            left.TargetPolicyRevision,
            left.SourceStorageContractRevision,
            left.TargetStorageContractRevision,
            left.ForceCopyAndRetainSource);
    }

    private static bool IdentityMatches(
        PathIdentitySnapshot left,
        PathIdentitySnapshot right)
    {
        if (left.Syntax != right.Syntax
            || left.CaseSensitivity != right.CaseSensitivity
            || left.RequestedMode != right.RequestedMode)
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                left.BoundaryPath,
                right.BoundaryPath,
                left.Semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool OptionalBoundaryMatches(
        string? left,
        string? right,
        FileSystemPathSemantics semantics)
    {
        var leftMissing = string.IsNullOrWhiteSpace(left);
        var rightMissing = string.IsNullOrWhiteSpace(right);
        if (leftMissing || rightMissing)
        {
            return leftMissing == rightMissing;
        }

        try
        {
            return FileSystemPathIdentity.AreEquivalent(left!, right!, semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
