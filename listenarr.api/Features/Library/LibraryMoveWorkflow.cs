/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using Listenarr.Application.Common;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    internal sealed record MoveEnqueuedResponse(
        string Message,
        Guid JobId,
        string Target);

    internal sealed record MoveJobStatusResponse(
        Guid Id,
        int AudiobookId,
        MoveJobStatus Status,
        MoveJobPhase Phase,
        double Progress,
        string? RequestedPath,
        string? Error,
        int AttemptCount,
        DateTime EnqueuedAt,
        DateTime? UpdatedAt,
        DateTime? NextAttemptAt,
        string RecoveryDisposition,
        bool CanRetry);

    internal sealed record MoveRecoveryStateResponse(
        bool HasUnresolvedMove,
        string Disposition,
        Guid? JobId,
        MoveJobStatus? Status,
        MoveJobPhase? Phase,
        string? RequestedPath,
        string? Error,
        bool CanRetry,
        IReadOnlyList<Guid> BlockingJobIds);

    public sealed partial class LibraryMoveWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMoveQueueService? _moveQueueService;
        private readonly IFileSystem _fileSystem;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly IMoveCleanupBoundaryResolver _cleanupBoundaryResolver;
        private readonly IAudiobookDestinationRewriteService _destinationRewriteService;
        private readonly IFilesystemMutationCoordinator _mutationCoordinator;
        private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
        private readonly ILibraryFilesystemMutationGate _filesystemMutationGate;
        private readonly ILogger<LibraryMoveWorkflow> _logger;

        public LibraryMoveWorkflow(
            IAudiobookRepository repo,
            IServiceScopeFactory scopeFactory,
            IFileSystem fileSystem,
            ILogger<LibraryMoveWorkflow> logger,
            IFileSystemSemanticsResolver semanticsResolver,
            IMoveCleanupBoundaryResolver cleanupBoundaryResolver,
            IAudiobookDestinationRewriteService destinationRewriteService,
            IFilesystemMutationCoordinator mutationCoordinator,
            IAudiobookOperationCoordinator audiobookOperationCoordinator,
            ILibraryFilesystemMutationGate filesystemMutationGate,
            IMoveQueueService? moveQueueService = null)
        {
            _repo = repo;
            _scopeFactory = scopeFactory;
            _fileSystem = fileSystem;
            _logger = logger;
            _semanticsResolver = semanticsResolver;
            _cleanupBoundaryResolver = cleanupBoundaryResolver;
            _destinationRewriteService = destinationRewriteService;
            _mutationCoordinator = mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
            _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
            _filesystemMutationGate = filesystemMutationGate
                ?? throw new ArgumentNullException(nameof(filesystemMutationGate));
            _moveQueueService = moveQueueService;
        }

        public async Task<IActionResult> EnqueueAsync(
            int id,
            LibraryController.MoveRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
            if (request == null) return new BadRequestObjectResult(new { message = "Request body is required" });

            if (string.IsNullOrEmpty(request.DestinationPath))
            {
                return new BadRequestObjectResult(new { message = "DestinationPath is required" });
            }

            // Preserve valid Unix path-segment whitespace, but reject values that only become
            // absolute after trimming accidental leading whitespace. Otherwise move would treat
            // " /books/Title" as a relative child folder under the configured destination root.
            if (FileUtils.HasLeadingWhitespaceBeforeRootedPath(request.DestinationPath))
            {
                return new BadRequestObjectResult(new { message = "DestinationPath is invalid: leading whitespace before an absolute path is not allowed." });
            }

            if (request.MoveFiles == false)
            {
                try
                {
                    await _destinationRewriteService.RewriteDestinationAsync(
                        id,
                        request.DestinationPath,
                        request.SourcePath,
                        cancellationToken);
                    return new OkObjectResult(new { message = "Destination updated" });
                }
                catch (ListenarrApplicationException ex)
                {
                    return ToApplicationExceptionResult(ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to update BasePath for audiobook {AudiobookId}", id);
                    return new ObjectResult(new
                    {
                        message = "Failed to update BasePath",
                        code = "destination_update_failed"
                    })
                    {
                        StatusCode = StatusCodes.Status500InternalServerError
                    };
                }
            }

            _filesystemMutationGate.EnsureReady();

            try
            {
                var recovery = await _moveQueueService.GetRecoveryStateForAudiobookAsync(
                    id,
                    cancellationToken);
                if (recovery.BlocksFilesystemMutation)
                {
                    return MoveRecoveryConflict(recovery);
                }
            }
            catch (PersistenceException ex)
            {
                _logger.LogError(
                    ex,
                    "Move queue persistence failed while checking unresolved move state for audiobook {AudiobookId}",
                    id);
                return MoveQueuePersistenceUnavailableResult();
            }

            return await _mutationCoordinator.ExecuteExclusiveAsync(
                token => EnqueuePhysicalAsync(id, request, token),
                cancellationToken);
        }

        public async Task<IActionResult> GetActiveAsync(
            CancellationToken cancellationToken = default)
        {
            if (_moveQueueService == null)
            {
                return new NotFoundObjectResult(new { message = "Move queue not available" });
            }

            var jobs = await _moveQueueService.GetActiveJobsAsync(cancellationToken);
            return new OkObjectResult(jobs.Select(ToStatusResponse).ToList());
        }

        public async Task<IActionResult> GetStatusAsync(
            string jobId,
            CancellationToken cancellationToken = default)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return new BadRequestObjectResult(new { message = "Invalid jobId" });
            var job = await _moveQueueService.GetJobAsync(gid, cancellationToken);
            if (job != null)
            {
                _logger.LogInformation("Queried move job {JobId} status: {Status}", gid, job.Status);
                return new OkObjectResult(ToStatusResponse(job));
            }

            return new NotFoundObjectResult(new { message = "Job not found" });
        }

        private static ConflictObjectResult MoveRecoveryConflict(MoveRecoveryState recovery)
        {
            var (code, message) = recovery.Disposition switch
            {
                MoveRecoveryDisposition.InProgress => (
                    "move_already_active",
                    "A move is already in progress for this audiobook. Wait for it to finish before changing the destination again."),
                MoveRecoveryDisposition.RetryAvailable => (
                    "move_recovery_required",
                    "An interrupted move still owns this audiobook's filesystem state. Resume that move before changing the destination again."),
                MoveRecoveryDisposition.OperatorRepairRequired => (
                    "move_repair_required",
                    "A previous move left unresolved filesystem state that requires repair before another move can start."),
                MoveRecoveryDisposition.Ambiguous => (
                    "move_recovery_ambiguous",
                    "Multiple move jobs contain unresolved filesystem state. Operator reconciliation is required before another move can start."),
                _ => (
                    "move_recovery_required",
                    "An unresolved move must be completed before another move can start.")
            };

            return new ConflictObjectResult(new
            {
                message,
                code,
                jobId = recovery.JobId,
                status = recovery.Status,
                requestedPath = recovery.RequestedPath,
                recoveryDisposition = recovery.Disposition.ToString(),
                canRetry = recovery.CanRetry,
                blockingJobIds = recovery.BlockingJobIds
            });
        }

        private sealed class MoveRecoveryConflictException(MoveRecoveryState recovery)
            : Exception("An unresolved move blocks a new physical move.")
        {
            public MoveRecoveryState Recovery { get; } = recovery;
        }

        private static ObjectResult MoveQueuePersistenceUnavailableResult() =>
            new(new
            {
                message = "Move queue persistence is unavailable. Check database migrations.",
                code = "move_queue_persistence_unavailable"
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };

        private static MoveJobStatusResponse ToStatusResponse(MoveJob job)
        {
            var disposition = MoveRecoveryPolicy.GetDisposition(job);
            return new MoveJobStatusResponse(
                job.Id,
                job.AudiobookId,
                job.Status,
                job.Phase,
                MoveJobPublicProjection.CalculateProgress(job, job.Status),
                job.RequestedPath,
                MoveJobPublicProjection.ToError(job),
                job.AttemptCount,
                job.EnqueuedAt,
                job.UpdatedAt,
                job.NextAttemptAt,
                disposition.ToString(),
                disposition == MoveRecoveryDisposition.RetryAvailable);
        }

        public async Task<IActionResult> GetRecoveryStateAsync(
            int audiobookId,
            CancellationToken cancellationToken = default)
        {
            if (_moveQueueService == null)
            {
                return new NotFoundObjectResult(new { message = "Move queue not available" });
            }

            var recovery = await _moveQueueService.GetRecoveryStateForAudiobookAsync(
                audiobookId,
                cancellationToken);
            return new OkObjectResult(new MoveRecoveryStateResponse(
                recovery.BlocksFilesystemMutation,
                recovery.Disposition.ToString(),
                recovery.JobId,
                recovery.Status,
                recovery.Phase,
                recovery.RequestedPath,
                recovery.Error == null
                    ? null
                    : MoveJobPublicProjection.ToError(recovery.Error, MoveFailureKind.Unknown),
                recovery.CanRetry,
                recovery.BlockingJobIds));
        }

        public async Task<IActionResult> RequeueAsync(
            string jobId,
            CancellationToken cancellationToken = default)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return new BadRequestObjectResult(new { message = "Invalid jobId" });

            _filesystemMutationGate.EnsureReady();

            Guid? newJobId;
            try
            {
                var existing = await _moveQueueService.GetJobAsync(gid, cancellationToken);
                if (existing == null)
                {
                    return new NotFoundObjectResult(new { message = "Move job not found" });
                }

                var disposition = MoveRecoveryPolicy.GetDisposition(existing);
                if (existing.Status != MoveJobStatus.Queued
                    && disposition != MoveRecoveryDisposition.RetryAvailable)
                {
                    return new ConflictObjectResult(new
                    {
                        message = "This move cannot be retried automatically because its persisted recovery evidence requires operator repair.",
                        code = "move_repair_required",
                        jobId = existing.Id,
                        status = existing.Status,
                        recoveryDisposition = disposition.ToString(),
                        canRetry = false
                    });
                }

                newJobId = await _moveQueueService.RequeueMoveAsync(
                    gid,
                    cancellationToken);
            }
            catch (MoveRelocationConflictException)
            {
                return new ConflictObjectResult(new
                {
                    message = "The move overlaps an active root folder relocation. Retry after the relocation completes.",
                    code = "move_relocation_conflict"
                });
            }

            if (newJobId == null)
            {
                return new BadRequestObjectResult(new { message = "Unable to requeue job (not found or invalid status)" });
            }

            return new AcceptedResult(string.Empty, new { message = "Requeued move job", jobId = newJobId });
        }
    }
}
