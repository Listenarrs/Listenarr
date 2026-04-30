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

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Service for managing download post-processing queue with retry capabilities
    /// </summary>
    public class DownloadProcessingQueueService : IDownloadProcessingQueueService
    {
        private readonly IDownloadProcessingJobRepository _jobRepository;
        private readonly ILogger<DownloadProcessingQueueService> _logger;
        private readonly DownloadProcessingChannel? _channel;

        public DownloadProcessingQueueService(
            IDownloadProcessingJobRepository jobRepository,
            ILogger<DownloadProcessingQueueService> logger,
            DownloadProcessingChannel? channel = null)
        {
            _jobRepository = jobRepository;
            _logger = logger;
            _channel = channel;
        }

        public async Task<string> QueueDownloadProcessingAsync(string downloadId, string sourcePath, string? downloadClientId = null)
        {
            var now = DateTime.UtcNow;
            var recentCompletedCutoff = now.AddSeconds(-300);

            var existingActive = await _jobRepository.GetActiveByDownloadIdAsync(downloadId);
            if (existingActive != null)
            {
                _logger.LogInformation("Duplicate enqueue prevented - returning existing active job {JobId} for download {DownloadId}", existingActive.Id, downloadId);
                return existingActive.Id;
            }

            var recentCompleted = await _jobRepository.GetRecentCompletedByDownloadIdAsync(downloadId, recentCompletedCutoff);
            if (recentCompleted != null)
            {
                _logger.LogInformation("Download {DownloadId} has a recent completed job {JobId} (within cooldown), not queuing new job", downloadId, recentCompleted.Id);
                return recentCompleted.Id;
            }

            var job = new DownloadProcessingJob
            {
                DownloadId = downloadId,
                JobType = ProcessingJobType.MoveOrCopyFile,
                SourcePath = sourcePath,
                DownloadClientId = downloadClientId ?? string.Empty,
                Priority = 5,
                Status = ProcessingJobStatus.Pending
            };

            job = await _jobRepository.AddAsync(job);
            _logger.LogInformation("Queued download {DownloadId} for post-processing: {JobId}", downloadId, job.Id);

            try
            {
                if (_channel != null)
                {
                    await _channel.EnqueueJobAsync(job.Id);
                    _logger.LogDebug("Published job {JobId} to in-memory processing channel", job.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to publish job {JobId} to processing channel", job.Id);
            }

            return job.Id;
        }

        public async Task<DownloadProcessingJob?> GetNextJobAsync()
            => await _jobRepository.GetNextPendingAsync();

        public async Task<List<DownloadProcessingJob>> GetRetryJobsAsync()
            => await _jobRepository.GetDueRetryJobsAsync();

        public async Task UpdateJobAsync(DownloadProcessingJob job)
            => await _jobRepository.UpdateAsync(job);

        public async Task<DownloadProcessingJob?> GetJobAsync(string jobId)
            => await _jobRepository.GetByIdAsync(jobId);

        public async Task<List<DownloadProcessingJob>> GetJobsForDownloadAsync(string downloadId)
            => await _jobRepository.GetByDownloadIdAsync(downloadId);

        public async Task<QueueStats> GetStatsAsync()
            => await _jobRepository.GetStatsAsync();

        public async Task CleanupOldJobsAsync(int retentionDays = 7)
            => await _jobRepository.CleanupOldJobsAsync(retentionDays);

        public async Task<List<DownloadProcessingJob>> GetRecentActivityAsync(int count = 50)
            => await _jobRepository.GetRecentAsync(count);
    }
}
