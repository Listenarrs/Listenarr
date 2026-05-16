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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks
{
    public class ScanQueueService : IScanQueueService
    {
        private readonly ConcurrentDictionary<Guid, ScanJob> _jobs = new();
        private readonly Channel<ScanJob> _channel = Channel.CreateUnbounded<ScanJob>();
        private readonly ILogger<ScanQueueService> _logger;

        public ScanQueueService(ILogger<ScanQueueService> logger)
        {
            _logger = logger;
        }

        public async Task<Guid> EnqueueScanAsync(Audiobook audiobook, string? path = null)
        {
            // Deduplicate: if there's already a job for the same audiobook and path that is
            // queued/processing/completed, return that job id instead of creating a duplicate.
            try
            {
                var existing = _jobs.Values.FirstOrDefault(j =>
                {
                    if (j.AudiobookId != audiobook.Id) return false;
                    bool bothNull = j.Path == null && path == null;
                    bool bothMatch = j.Path != null && path != null && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase);
                    return bothNull || bothMatch;
                });

                // Only dedupe when an existing job is actively queued or processing.
                // If a previous job Completed or Failed, allow a new job to be created so
                // explicit re-scans can be scheduled.
                if (existing != null &&
                    (string.Equals(existing.Status, "Queued", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(existing.Status, "Processing", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("Found active scan job {JobId} for audiobook {AudiobookId} (path: {Path}) with status {Status}; deduping and returning existing job id", existing.Id, audiobook.Id, LogRedaction.SanitizeFilePath(path), existing.Status);
                    return existing.Id;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // If dedupe check fails for any reason, fall back to enqueueing a new job
                _logger.LogWarning(ex, "Failed while checking existing scan jobs for dedupe; will enqueue new job");
            }

            var job = new ScanJob { AudiobookId = audiobook.Id, Path = path };
            _jobs[job.Id] = job;
            _logger.LogInformation("Enqueueing scan job {JobId} for audiobook {AudiobookId} (path: {Path})", job.Id, audiobook.Id, LogRedaction.SanitizeFilePath(path));
            await _channel.Writer.WriteAsync(job);
            _logger.LogInformation("Scan job {JobId} written to channel", job.Id);
            return job.Id;
        }

        public bool TryGetJob(Guid id, out ScanJob? job) => _jobs.TryGetValue(id, out job);

        public ChannelReader<ScanJob> Reader => _channel.Reader;

        public void UpdateJobStatus(Guid id, string status, string? error = null, int? found = null, int? created = null)
        {
            if (_jobs.TryGetValue(id, out var job))
            {
                job.Status = status;
                job.Error = error;
                // store optional counters in the Error field or extend ScanJob if needed; keep simple for now
                _jobs[id] = job;
                _logger.LogInformation("Updated scan job {JobId} status to {Status}", id, status);
            }
            else
            {
                _logger.LogWarning("Attempted to update unknown scan job {JobId} to {Status}", id, status);
            }
        }

        public async Task<Guid?> RequeueScanAsync(Guid jobId)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                _logger.LogWarning("Attempted to requeue unknown scan job {JobId}", jobId);
                return null;
            }

            // Allow requeue for Failed jobs or Completed (explicit re-run)
            if (!CanRequeueJobStatus(job.Status))
            {
                _logger.LogInformation("Scan job {JobId} has status {Status} and cannot be requeued", jobId, job.Status);
                return null;
            }

            var newJob = new ScanJob { AudiobookId = job.AudiobookId, Path = job.Path };
            _jobs[newJob.Id] = newJob;
            _logger.LogInformation("Requeueing scan job {OldJobId} as new job {NewJobId} for audiobook {AudiobookId}", jobId, newJob.Id, job.AudiobookId);
            await _channel.Writer.WriteAsync(newJob);
            return newJob.Id;
        }

        /// <summary>
        /// Determines if a job with the given status can be requeued.
        /// </summary>
        private static bool CanRequeueJobStatus(string status)
        {
            return string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase);
        }
    }
}

