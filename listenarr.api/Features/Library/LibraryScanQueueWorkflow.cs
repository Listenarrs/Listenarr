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
    internal sealed record ScanJobStatusResponse(
        Guid Id,
        int AudiobookId,
        string Status,
        string? Error,
        DateTime EnqueuedAt,
        bool CanRequeue);

    public sealed class LibraryScanQueueWorkflow
    {
        private readonly IScanQueueService? _scanQueueService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILibraryFilesystemMutationGate _filesystemMutationGate;
        private readonly ILogger<LibraryScanQueueWorkflow> _logger;

        public LibraryScanQueueWorkflow(
            IServiceScopeFactory scopeFactory,
            ILibraryFilesystemMutationGate filesystemMutationGate,
            ILogger<LibraryScanQueueWorkflow> logger,
            IScanQueueService? scanQueueService = null)
        {
            _scopeFactory = scopeFactory;
            _filesystemMutationGate = filesystemMutationGate
                ?? throw new ArgumentNullException(nameof(filesystemMutationGate));
            _logger = logger;
            _scanQueueService = scanQueueService;
        }

        public async Task<IActionResult?> TryEnqueueAsync(
            Audiobook audiobook,
            string? requestedPath,
            PathIdentitySnapshot? pathIdentity,
            ScanPathPhysicalIdentity? physicalIdentity,
            bool isAuthoritativeScope)
        {
            _filesystemMutationGate.EnsureReady();

            if (_scanQueueService == null)
            {
                return null;
            }

            try
            {
                var jobId = await _scanQueueService.EnqueueScanAsync(
                    new ScanEnqueueCommand(
                        audiobook,
                        requestedPath,
                        pathIdentity,
                        physicalIdentity,
                        IsAuthoritativeScope: isAuthoritativeScope,
                        AuthorizationMode: string.IsNullOrWhiteSpace(requestedPath)
                            ? ScanAuthorizationMode.ResolveCurrentAudiobookPath
                            : ScanAuthorizationMode.PreauthorizedPath));
                _logger.LogInformation("Enqueued scan job {JobId} for audiobook {AudiobookId}", jobId, audiobook.Id);
                await BroadcastQueuedAsync(jobId, audiobook.Id);

                return new AcceptedResult(string.Empty, new { message = "Scan enqueued", jobId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to enqueue scan job for audiobook {AudiobookId}", audiobook.Id);
                return new ObjectResult(new
                {
                    message = "Failed to enqueue scan job",
                    code = "scan_enqueue_failed"
                })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        public IActionResult GetStatus(string jobId)
        {
            if (_scanQueueService == null)
            {
                return new NotFoundObjectResult(new { message = "Scan queue not available" });
            }

            if (!Guid.TryParse(jobId, out var parsedJobId))
            {
                return new BadRequestObjectResult(new { message = "Invalid jobId" });
            }

            if (_scanQueueService.TryGetJob(parsedJobId, out var job))
            {
                _logger.LogInformation("Queried scan job {JobId} status: {Status}", parsedJobId, job!.Status);
                return new OkObjectResult(new ScanJobStatusResponse(
                    job.Id,
                    job.AudiobookId,
                    job.Status,
                    ScanJobPublicError.FromInternal(job.Error),
                    job.EnqueuedAt,
                    CanRequeueJob(job.Status)));
            }

            return new NotFoundObjectResult(new { message = "Job not found" });
        }

        public async Task<IActionResult> RequeueAsync(string jobId)
        {
            if (_scanQueueService == null)
            {
                return new NotFoundObjectResult(new { message = "Scan queue not available" });
            }

            if (!Guid.TryParse(jobId, out var parsedJobId))
            {
                return new BadRequestObjectResult(new { message = "Invalid jobId" });
            }

            _filesystemMutationGate.EnsureReady();

            var newJobId = await _scanQueueService.RequeueScanAsync(parsedJobId);
            if (newJobId == null)
            {
                return new BadRequestObjectResult(new { message = "Unable to requeue job (not found or invalid status)" });
            }

            await BroadcastQueuedAsync(newJobId.Value, audiobookId: null);
            return new AcceptedResult(string.Empty, new { message = "Requeued scan job", jobId = newJobId });
        }

        private static bool CanRequeueJob(string status) =>
            string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase);

        private async Task BroadcastQueuedAsync(Guid jobId, int? audiobookId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<IHubBroadcaster>();
                var job = new { jobId = jobId.ToString(), audiobookId, status = "Queued", enqueuedAt = DateTime.UtcNow };
                await hub.BroadcastAsync(RealtimeHubTarget.Downloads, "ScanJobUpdate", job);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(
                    ex,
                    "Scan job {JobId} was committed before its realtime broadcast was canceled",
                    jobId);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(ex, "Failed to broadcast ScanJobUpdate for job {JobId}", jobId);
            }
        }
    }
}
