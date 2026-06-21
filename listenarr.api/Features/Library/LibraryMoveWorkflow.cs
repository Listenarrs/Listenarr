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

using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryMoveWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMoveQueueService? _moveQueueService;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibraryMoveWorkflow> _logger;

        public LibraryMoveWorkflow(
            IAudiobookRepository repo,
            IServiceScopeFactory scopeFactory,
            IFileSystem fileSystem,
            ILogger<LibraryMoveWorkflow> logger,
            IMoveQueueService? moveQueueService = null)
        {
            _repo = repo;
            _scopeFactory = scopeFactory;
            _fileSystem = fileSystem;
            _logger = logger;
            _moveQueueService = moveQueueService;
        }

        public async Task<IActionResult> EnqueueAsync(int id, LibraryController.MoveRequest request)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null) return new NotFoundObjectResult(new { message = "Audiobook not found" });
            if (request == null) return new BadRequestObjectResult(new { message = "Request body is required" });

            if (string.IsNullOrEmpty(request.DestinationPath))
            {
                return new BadRequestObjectResult(new { message = "DestinationPath is required" });
            }

            if (FileUtils.IsPathInvalidForCurrentOs(request.DestinationPath))
            {
                return new BadRequestObjectResult(new { message = "DestinationPath is not valid for this operating system" });
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();

                var destinationIsRooted = Path.IsPathRooted(request.DestinationPath!);
                var final = FileUtils.CombineWithOptionalBase(settings.OutputPath, request.DestinationPath!);
                final = FileUtils.NormalizeStoredPath(final);
                if (!destinationIsRooted
                    && !string.IsNullOrWhiteSpace(settings.OutputPath)
                    && !_fileSystem.TryValidateMutationTarget(final, [settings.OutputPath], out final, out var finalReason))
                {
                    _logger.LogWarning(
                        "Blocked move destination for audiobook {AudiobookId}: {Destination}. Reason: {Reason}",
                        id,
                        final,
                        finalReason);
                    return new BadRequestObjectResult(new { message = "DestinationPath must be inside the configured output path" });
                }

                if (request.MoveFiles == false)
                {
                    try
                    {
                        audiobook.BasePath = final;
                        await _repo.UpdateAsync(audiobook);
                        _logger.LogInformation("Updated BasePath for audiobook {AudiobookId} without moving files: {BasePath}", id, final);
                        return new OkObjectResult(new { message = "Destination updated" });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Failed to update BasePath for audiobook {AudiobookId}", id);
                        return new ObjectResult(new { message = "Failed to update BasePath", error = ex.Message })
                        {
                            StatusCode = StatusCodes.Status500InternalServerError
                        };
                    }
                }

                var sourcePath = !string.IsNullOrEmpty(request.SourcePath)
                    ? request.SourcePath
                    : audiobook.BasePath;

                if (string.IsNullOrEmpty(sourcePath))
                {
                    return new BadRequestObjectResult(new { message = "Source path not provided. Supply current source path in the Move request or ensure audiobook has a valid BasePath." });
                }

                if (FileUtils.IsPathInvalidForCurrentOs(sourcePath))
                {
                    return new BadRequestObjectResult(new { message = "Source path is not valid for this operating system." });
                }

                if (!_fileSystem.DirectoryExists(sourcePath))
                {
                    return new BadRequestObjectResult(new { message = "Source path does not exist. Ensure the audiobook's current BasePath exists or provide a valid SourcePath in the request." });
                }

                var targetParent = Path.GetDirectoryName(final);
                if (string.IsNullOrEmpty(targetParent))
                {
                    return new BadRequestObjectResult(new { message = "Invalid target path" });
                }

                try
                {
                    if (!_fileSystem.DirectoryExists(targetParent)) _fileSystem.CreateDirectory(targetParent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to access or create target parent {TargetParent}", targetParent);
                    return new BadRequestObjectResult(new { message = "Target parent path is not writable or unavailable" });
                }

                try
                {
                    var srcFull = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var tgtFull = Path.GetFullPath(final).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(srcFull, tgtFull, StringComparison.OrdinalIgnoreCase))
                    {
                        return new BadRequestObjectResult(new { message = "Source and target paths are identical; nothing to move." });
                    }
                }
                catch (Exception normalizeEx) when (
                    normalizeEx is ArgumentException
                    || normalizeEx is NotSupportedException
                    || normalizeEx is PathTooLongException
                    || normalizeEx is System.Security.SecurityException)
                {
                    _logger.LogDebug(normalizeEx, "Unable to normalize move paths for audiobook {AudiobookId}", id);
                }

                var jobId = await _moveQueueService.EnqueueMoveAsync(id, final, sourcePath);
                await BroadcastQueuedAsync(jobId, id);

                return new AcceptedResult(string.Empty, new { message = "Move enqueued", jobId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to enqueue move job for audiobook {AudiobookId}", id);
                return new ObjectResult(new { message = "Failed to enqueue move job", error = ex.Message })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
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
                _logger.LogInformation("Queried move job {JobId} status: {Status}", gid, job!.Status);
                return new OkObjectResult(job);
            }

            return new NotFoundObjectResult(new { message = "Job not found" });
        }

        public async Task<IActionResult> RequeueAsync(string jobId)
        {
            if (_moveQueueService == null) return new NotFoundObjectResult(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return new BadRequestObjectResult(new { message = "Invalid jobId" });

            var newJobId = await _moveQueueService.RequeueMoveAsync(gid);
            if (newJobId == null)
            {
                return new BadRequestObjectResult(new { message = "Unable to requeue job (not found or invalid status)" });
            }

            await BroadcastQueuedAsync(newJobId.Value, audiobookId: null);
            return new AcceptedResult(string.Empty, new { message = "Requeued move job", jobId = newJobId });
        }

        private async Task BroadcastQueuedAsync(Guid jobId, int? audiobookId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<IHubBroadcaster>();
                var job = new { jobId = jobId.ToString(), audiobookId, status = "Queued", enqueuedAt = DateTime.UtcNow };
                await hub.BroadcastAsync(RealtimeHubTarget.Downloads, "MoveJobUpdate", job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast MoveJobUpdate for job {JobId}", jobId);
            }
        }
    }
}
