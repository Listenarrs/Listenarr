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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Processing
{
    /// <summary>
    /// Service for managing download post-processing queue with retry capabilities
    /// </summary>
    public class DownloadProcessingJobService(
        IDownloadProcessingJobRepository jobRepository,
        ILogger<DownloadProcessingJobService> logger) : IDownloadProcessingJobService
    {
        public async Task<string> EnqueueAsync(Download download)
        {
            var now = DateTime.UtcNow;
            var recentCompletedCutoff = now.AddSeconds(-300);

            var existingActive = await jobRepository.GetActiveByDownloadIdAsync(download.Id);
            if (existingActive != null)
            {
                logger.LogInformation("Duplicate enqueue prevented - returning existing active job {JobId} for download {DownloadId}", existingActive.Id, download.Id);
                return existingActive.Id;
            }

            var recentCompleted = await jobRepository.GetRecentCompletedByDownloadIdAsync(download.Id, recentCompletedCutoff);
            if (recentCompleted != null)
            {
                logger.LogInformation("Download {DownloadId} has a recent completed job {JobId} (within cooldown), not queuing new job", download.Id, recentCompleted.Id);
                return recentCompleted.Id;
            }

            if (download.Status != DownloadStatus.Completed)
            {
                throw new InvalidOperationException($"Trying to enqueue an import job for download {download.Id} with status {download.Status}: Status should be Completed");
            }

            var job = new DownloadProcessingJob
            {
                DownloadId = download.Id,
                JobType = ProcessingJobType.MoveOrCopyFile,
                SourcePath = download.DownloadPath,
                DownloadClientId = download.DownloadClientId,
                Priority = 5,
                Status = ProcessingJobStatus.Pending,
                JobData = new Dictionary<string, object>
                {
                    ["CorrelationId"] = download.Id.ToUpperInvariant()
                }
            };

            try
            {
                job = await jobRepository.AddAsync(job);
            }
            catch (UniqueConstraintViolationException)
            {
                existingActive = await jobRepository.GetActiveByDownloadIdAsync(download.Id);
                if (existingActive != null)
                {
                    logger.LogInformation(
                        "Concurrent duplicate enqueue prevented - returning existing active job {JobId} for download {DownloadId}",
                        existingActive.Id,
                        download.Id);
                    return existingActive.Id;
                }

                throw;
            }
            logger.LogInformation("Queued download {DownloadId} for post-processing: {JobId}", download.Id, job.Id);
            return job.Id;
        }

        public async Task<DownloadProcessingJob?> GetNextJobAsync()
        {
            try
            {
                var jobs = await jobRepository.GetJobsByStatusAsync(ProcessingJobStatus.Pending);
                return jobs
                    .Where(j => j.NextRetryAt == null || j.NextRetryAt <= DateTime.UtcNow)
                    .OrderByDescending(j => j.Priority)
                    .ThenBy(j => j.CreatedAt)
                    .First();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<List<DownloadProcessingJob>> GetRetryJobsAsync()
            => await jobRepository.GetDueRetryJobsAsync();

        public async Task UpdateJobAsync(DownloadProcessingJob job)
            => await jobRepository.UpdateAsync(job);

        public async Task<DownloadProcessingJob?> GetJobAsync(string jobId)
            => await jobRepository.GetByIdAsync(jobId);

        public async Task<List<DownloadProcessingJob>> GetJobsForDownloadAsync(string downloadId)
            => await jobRepository.GetByDownloadIdAsync(downloadId);

        public async Task<QueueStats> GetStatsAsync()
            => await jobRepository.GetStatsAsync();

        public async Task CleanupOldJobsAsync(int retentionDays = 7)
            => await jobRepository.CleanupOldJobsAsync(retentionDays);

        public async Task<List<DownloadProcessingJob>> GetRecentActivityAsync(int count = 50)
            => await jobRepository.GetRecentAsync(count);

        /// <summary>
        /// Reset jobs that were stuck in Processing status from a previous session (e.g., after crash or restart).
        /// </summary>
        public async Task ResetStuckJobsAsync(CancellationToken cancellationToken = default)
        {
            var stuckJobs = await jobRepository.GetStuckProcessingJobsAsync(cancellationToken);
            if (stuckJobs.Count <= 0)
            {
                return;
            }

            logger.LogInformation("Found {Count} stuck jobs in Processing status, resetting to Pending", stuckJobs.Count);
            foreach (var job in stuckJobs)
            {
                await jobRepository.UpdateAsync(job.UnStuck("Reset from stuck Processing state after service restart"));
            }
        }
    }
}
