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
using System.Collections.Concurrent;
using System.Threading.Channels;
using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;


namespace Listenarr.Application.Audiobooks.Jobs
{
    public class MoveQueueService : IMoveQueueService
    {
        private readonly ConcurrentDictionary<Guid, MoveJob> _jobs = new();
        private readonly Channel<MoveJob> _channel = Channel.CreateUnbounded<MoveJob>();
        private readonly ILogger<MoveQueueService> _logger;
        private readonly IMoveQueuePersistence _persistence;
        private readonly IHubBroadcaster _hubBroadcaster;
        private readonly TimeProvider _timeProvider;

        public MoveQueueService(
            ILogger<MoveQueueService> logger,
            IMoveQueuePersistence persistence,
            IHubBroadcaster hubBroadcaster,
            TimeProvider timeProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _hubBroadcaster = hubBroadcaster ?? throw new ArgumentNullException(nameof(hubBroadcaster));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        public ChannelReader<MoveJob> Reader => _channel.Reader;

        public async Task<Guid> EnqueueMoveAsync(int audiobookId, string requestedPath, string? sourcePath = null)
        {
            var deduplicationKey = BuildDeduplicationKey(audiobookId, requestedPath);
            var existingDb = await _persistence.GetActiveByKeyAsync(deduplicationKey);

            if (existingDb != null)
            {
                _jobs[existingDb.Id] = existingDb;
                _logger.LogInformation("Found active move job {JobId} for audiobook {AudiobookId} to {Path}; deduping and returning existing job id", existingDb.Id, audiobookId, LogRedaction.SanitizeFilePath(requestedPath));
                return existingDb.Id;
            }

            var job = new MoveJob
            {
                AudiobookId = audiobookId,
                RequestedPath = requestedPath,
                ActiveDeduplicationKey = deduplicationKey,
                EnqueuedAt = _timeProvider.GetUtcNow().UtcDateTime,
                Status = "Queued",
                SourcePath = sourcePath
            };

            try
            {
                await _persistence.AddAsync(job);
            }
            catch (UniqueConstraintViolationException)
            {
                existingDb = await _persistence.GetActiveByKeyAsync(deduplicationKey);
                if (existingDb != null)
                {
                    _jobs[existingDb.Id] = existingDb;
                    return existingDb.Id;
                }

                throw;
            }

            _jobs[job.Id] = job;
            _logger.LogInformation("Enqueueing move job {JobId} for audiobook {AudiobookId} to {Path}", job.Id, audiobookId, LogRedaction.SanitizeFilePath(requestedPath));
            await _channel.Writer.WriteAsync(job);
            return job.Id;
        }

        public async Task<MoveJob?> GetJobAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (_jobs.TryGetValue(id, out var job)) return job;
            try
            {
                job = await _persistence.GetByIdAsync(id, cancellationToken);
                if (job != null) _jobs[id] = job;
                return job;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to retrieve move job {JobId}", id);
                return null;
            }
        }

        public async Task UpdateJobStatusAsync(
            Guid id,
            string status,
            string? error = null,
            CancellationToken cancellationToken = default)
        {
            var updatedAt = _timeProvider.GetUtcNow();
            if (_jobs.TryGetValue(id, out var job) && job != null)
            {
                job.Status = status;
                job.Error = error;
                job.UpdatedAt = updatedAt.UtcDateTime;
                job.ActiveDeduplicationKey = IsActive(status) ? job.ActiveDeduplicationKey : null;
                _jobs[id] = job;
            }

            try
            {
                var dbJob = await _persistence.GetByIdAsync(id, cancellationToken);
                await _persistence.UpdateStatusAsync(id, status, error, updatedAt, cancellationToken);

                // Broadcast status update to realtime clients so UI can react to Processing/Failed/Completed
                try
                {
                    var payload = new
                    {
                        jobId = id.ToString(),
                        audiobookId = dbJob?.AudiobookId ?? job?.AudiobookId,
                        status = status,
                        error = error,
                        target = dbJob?.RequestedPath ?? job?.RequestedPath,
                        updatedAt
                    };
                    // Fire and forget but block briefly to surface errors during development
                    await _hubBroadcaster.BroadcastAsync("MoveJobUpdate", payload, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to broadcast MoveJobUpdate for job {JobId}", id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist move job status change for {JobId}", id);
            }

            // Log error prominently if status is Failed
            if (status == "Failed" && !string.IsNullOrWhiteSpace(error))
            {
                _logger.LogError("Move job {JobId} FAILED with error: {Error}", id, error);
            }
            else
            {
                _logger.LogInformation("Updated move job {JobId} status to {Status}", id, status);
            }
        }

        public async Task<Guid?> RequeueMoveAsync(Guid jobId)
        {
            MoveJob? job = null;
            if (!_jobs.TryGetValue(jobId, out job))
            {
                try
                {
                    job = await _persistence.GetByIdAsync(jobId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to read move job from DB while requeueing {JobId}", jobId);
                }

                if (job == null)
                {
                    _logger.LogWarning("Attempted to requeue unknown move job {JobId}", jobId);
                    return null;
                }
            }

            if (!CanRequeueJobStatus(job.Status))
            {
                _logger.LogInformation("Move job {JobId} has status {Status} and cannot be requeued", jobId, job.Status);
                return null;
            }

            var newJobId = await EnqueueMoveAsync(job.AudiobookId, job.RequestedPath ?? string.Empty, job.SourcePath);
            _logger.LogInformation("Requeueing move job {OldJobId} as job {NewJobId} for audiobook {AudiobookId}", jobId, newJobId, job.AudiobookId);
            return newJobId;
        }

        private static bool CanRequeueJobStatus(string status)
        {
            return string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDeduplicationKey(int audiobookId, string? requestedPath)
        {
            var normalizedPath = (requestedPath ?? string.Empty)
                .Trim()
                .Replace('\\', '/')
                .TrimEnd('/')
                .ToUpperInvariant();
            return $"{audiobookId}:{normalizedPath}";
        }

        private static bool IsActive(string status) =>
            string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Processing", StringComparison.OrdinalIgnoreCase);
    }
}
