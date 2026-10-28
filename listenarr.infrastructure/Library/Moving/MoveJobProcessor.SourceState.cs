using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor
{
    private async Task<bool> TryHandleIdenticalEndpointsAsync(
        MoveJob job,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        AudiobookContentMoveService contentMoveService,
        CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.AreEquivalentEndpoints(
                source,
                sourceIdentity,
                target,
                targetIdentity))
        {
            return false;
        }

        try
        {
            await contentMoveService.VerifyNoFilesystemMoveStartedAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    job.Id,
                    job.DeleteEmptySource,
                    sourceIdentity.Semantics,
                    targetIdentity.Semantics,
                    CreateLeaseToken(job)),
                cancellationToken);
        }
        catch (MoveNeedsAttentionException exception)
        {
            await UpdateJobStatusAsync(
                job,
                MoveJobStatus.NeedsAttention,
                exception.Message,
                cancellationToken);
            metrics.Increment("worker.move.job.needs_attention");
            logger.LogWarning(
                exception,
                "Identical-endpoint move job {JobId} has execution evidence and was preserved",
                job.Id);
            return true;
        }
        catch (Exception exception) when (IsTransientFilesystemException(exception))
        {
            await ScheduleTransientRetryAsync(
                job,
                $"Identical-endpoint evidence verification will be retried: {exception.Message}",
                exception,
                "Move job {JobId} could not verify that filesystem execution never started",
                cancellationToken);
            return true;
        }

        await UpdateJobStatusAsync(
            job,
            MoveJobStatus.Superseded,
            "Superseded because the persisted source and target endpoints are identical.",
            cancellationToken);
        metrics.Increment("worker.move.job.skipped");
        return true;
    }

    private async Task<Audiobook?> ValidateSourceStateBeforeMutationAsync(
        MoveJob job,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        AudiobookContentMoveResult? recoveredMove,
        bool hasFilesystemExecutionEvidence,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
        var currentAudiobook = await repository.GetByIdAsync(job.AudiobookId);
        if (currentAudiobook == null)
        {
            await UpdateJobStatusAsync(
                job,
                MoveJobStatus.Failed,
                "Audiobook not found",
                cancellationToken);
            metrics.Increment("worker.move.job.failed");
            return null;
        }

        var currentPath = currentAudiobook.BasePath;
        if (!string.IsNullOrWhiteSpace(currentPath)
            && !IsValidAbsolutePath(currentPath, sourceIdentity.Syntax)
            && !IsValidAbsolutePath(currentPath, targetIdentity.Syntax))
        {
            await MarkSourceStateNeedsAttentionAsync(
                job,
                "The audiobook's current source path is malformed, so the queued move cannot be proven safe.",
                cancellationToken);
            return null;
        }

        var matchesSource = string.IsNullOrWhiteSpace(currentPath)
            || PathsMatch(currentPath, source, sourceIdentity.Semantics)
            || IsSourceInsideMetadataBasePath(
                source,
                currentPath,
                sourceIdentity.Semantics);
        var matchesTarget = !string.IsNullOrWhiteSpace(currentPath)
            && PathsMatch(currentPath, target, targetIdentity.Semantics);
        var hasVerifiedRecovery = recoveredMove != null;
        var hasDurableExecutionEvidence = hasFilesystemExecutionEvidence
            || MoveRecoveryPolicy.HasFilesystemExecutionEvidence(job);
        var hasRecoveryEvidence = hasVerifiedRecovery || hasDurableExecutionEvidence;
        var hasAdvancedDurablePhase = job.Phase > MoveJobPhase.Planned;

        if (matchesSource)
        {
            // Once this job has durable filesystem execution evidence, its persisted
            // markerless journal is the authoritative description of expected source
            // drift. Rebuilding the pre-move source plan here would reject legitimate
            // partial copies, native renames, and source-cleanup recovery. The content
            // mover revalidates that journal, endpoint generations, and target proof
            // before performing or resuming any mutation.
            if (!hasRecoveryEvidence)
            {
                bool currentManifestMatches;
                try
                {
                    currentManifestMatches = await CurrentTrackedManifestMatchesAsync(
                        scope.ServiceProvider,
                        currentAudiobook,
                        job,
                        source,
                        sourceIdentity,
                        cancellationToken);
                }
                catch (ApplicationUnavailableException exception)
                {
                    await ScheduleTransientRetryAsync(
                        job,
                        $"Move source ownership verification will be retried: {exception.SafeDetail}",
                        exception,
                        "Move job {JobId} could not inspect its tracked source manifest",
                        cancellationToken);
                    return null;
                }
                catch (Exception exception) when (IsTransientFilesystemException(exception))
                {
                    await ScheduleTransientRetryAsync(
                        job,
                        $"Move source ownership verification will be retried: {exception.Message}",
                        exception,
                        "Move job {JobId} could not inspect its tracked source manifest",
                        cancellationToken);
                    return null;
                }

                if (!currentManifestMatches)
                {
                    await MarkSourceStateNeedsAttentionAsync(
                        job,
                        "The audiobook's current tracked-file ownership no longer matches the queued move source manifest.",
                        cancellationToken);
                    return null;
                }
            }

            return currentAudiobook;
        }

        if (matchesTarget && hasVerifiedRecovery)
        {
            return currentAudiobook;
        }

        if (matchesTarget && !hasRecoveryEvidence)
        {
            var targetPresence = ProbeDirectoryPresence(target);
            if (targetPresence == DirectoryPresence.Unavailable)
            {
                await ScheduleTransientRetryAsync(
                    job,
                    "Move target presence verification will be retried because the target is temporarily unavailable.",
                    new IOException("The move target directory could not be inspected safely."),
                    "Move job {JobId} could not verify whether its requested target exists",
                    cancellationToken);
                return null;
            }
            if (targetPresence == DirectoryPresence.Missing)
            {
                await MarkSourceStateNeedsAttentionAsync(
                    job,
                    "The audiobook points at the requested target, but the target does not exist and no move execution evidence is available.",
                    cancellationToken);
                return null;
            }
        }

        if (hasRecoveryEvidence || hasAdvancedDurablePhase)
        {
            await MarkSourceStateNeedsAttentionAsync(
                job,
                "The audiobook path changed after filesystem execution began. Recovery evidence was preserved for operator review.",
                cancellationToken);
            return null;
        }

        await UpdateJobStatusAsync(
            job,
            MoveJobStatus.Superseded,
            matchesTarget
                ? "Superseded because the audiobook already points at the requested target and no filesystem execution evidence exists."
                : "Superseded because the audiobook source path changed before filesystem execution began.",
            cancellationToken);
        metrics.Increment("worker.move.job.skipped");
        logger.LogInformation(
            "Superseded stale move job {JobId} for audiobook {AudiobookId} before filesystem mutation",
            job.Id,
            job.AudiobookId);
        return null;
    }

    private async Task<bool?> HasFilesystemExecutionEvidenceAsync(
        MoveJob job,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        string? cleanupBoundary,
        AudiobookContentMoveService contentMoveService,
        CancellationToken cancellationToken)
    {
        try
        {
            await contentMoveService.VerifyNoFilesystemMoveStartedAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    job.Id,
                    job.DeleteEmptySource,
                    sourceIdentity.Semantics,
                    targetIdentity.Semantics,
                    CreateLeaseToken(job),
                    cleanupBoundary),
                cancellationToken);
            return false;
        }
        catch (MoveNeedsAttentionException)
        {
            return true;
        }
        catch (Exception exception) when (IsTransientFilesystemException(exception))
        {
            await ScheduleTransientRetryAsync(
                job,
                $"Move execution evidence verification will be retried: {exception.Message}",
                exception,
                "Move job {JobId} could not inspect its filesystem execution evidence",
                cancellationToken);
            return null;
        }
    }

    private static DirectoryPresence ProbeDirectoryPresence(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) == 0
                    ? DirectoryPresence.Present
                    : DirectoryPresence.Missing;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            return DirectoryPresence.Missing;
        }
        catch (System.ComponentModel.Win32Exception exception) when (
            OperatingSystem.IsWindows()
                ? exception.NativeErrorCode is 2 or 3
                : exception.NativeErrorCode == 2)
        {
            return DirectoryPresence.Missing;
        }
        catch (Exception exception) when (IsTransientFilesystemException(exception))
        {
            return DirectoryPresence.Unavailable;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            return DirectoryPresence.Missing;
        }
    }

    private enum DirectoryPresence
    {
        Present,
        Missing,
        Unavailable
    }

    private static bool IsSourceInsideMetadataBasePath(
        string source,
        string? metadataBasePath,
        FileSystemPathSemantics sourceSemantics)
    {
        if (string.IsNullOrWhiteSpace(metadataBasePath))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.IsSameOrInside(
                source,
                metadataBasePath,
                sourceSemantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static async Task<bool> CurrentTrackedManifestMatchesAsync(
        IServiceProvider services,
        Audiobook currentAudiobook,
        MoveJob job,
        string source,
        PathIdentitySnapshot sourceIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentManifest = await services
                .GetRequiredService<IMoveSourcePlanService>()
                .BuildPlanAsync(
                    new AudiobookPathReferenceSnapshot(
                        currentAudiobook.Id,
                        currentAudiobook.BasePath,
                        currentAudiobook.FilePath),
                    cancellationToken);
            return FileSystemPathIdentity.AreEquivalent(
                    currentManifest.SourceRoot,
                    source,
                    sourceIdentity.Semantics)
                && MoveManifestIdentity.SourceManifestShapesMatch(
                    currentManifest.Entries,
                    job.Entries,
                    sourceIdentity.Semantics);
        }
        catch (Exception exception) when (exception is
            ApplicationConflictException or ArgumentException
            or InvalidOperationException or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private async Task MarkSourceStateNeedsAttentionAsync(
        MoveJob job,
        string message,
        CancellationToken cancellationToken)
    {
        await UpdateJobStatusAsync(
            job,
            MoveJobStatus.NeedsAttention,
            message,
            cancellationToken);
        metrics.Increment("worker.move.job.needs_attention");
        logger.LogWarning(
            "Move job {JobId} for audiobook {AudiobookId} failed its authoritative source-state fence: {Reason}",
            job.Id,
            job.AudiobookId,
            message);
    }

    private async Task<string?> ResolvePersistedEndpointAsync(
        MoveJob job,
        string path,
        string endpoint,
        CancellationToken cancellationToken)
    {
        PathIdentitySnapshot identity;
        var hasIdentity = string.Equals(endpoint, "target", StringComparison.Ordinal)
            ? job.TryGetTargetIdentity(out identity)
            : job.TryGetSourceIdentity(out identity);
        var resolved = hasIdentity
            ? FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                path,
                identity,
                out var absolutePath,
                out var reason)
            : FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out absolutePath,
                out reason);
        if (resolved)
        {
            return absolutePath;
        }

        await UpdateJobStatusAsync(
            job,
            MoveJobStatus.NeedsAttention,
            $"The persisted {endpoint} path is invalid: {reason}",
            cancellationToken);
        metrics.Increment("worker.move.job.needs_attention");
        return null;
    }

    private static bool IsValidAbsolutePath(string path, FileSystemPathSyntax syntax)
    {
        try
        {
            _ = FileSystemPathIdentity.Canonicalize(path, syntax);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool PathsMatch(
        string path,
        string expectedPath,
        FileSystemPathSemantics semantics)
    {
        try
        {
            return FileSystemPathIdentity.AreEquivalent(path, expectedPath, semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
