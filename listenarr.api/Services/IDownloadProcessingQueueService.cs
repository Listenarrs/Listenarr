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
    /// Service for managing download post-processing queue
    /// </summary>
    public interface IDownloadProcessingQueueService
    {
        /// <summary>
        /// Queue a download for post-processing
        /// </summary>
        Task<string> QueueDownloadProcessingAsync(string downloadId, string sourcePath, string? downloadClientId = null);

        /// <summary>
        /// Get the next pending job to process
        /// </summary>
        Task<DownloadProcessingJob?> GetNextJobAsync();

        /// <summary>
        /// Get jobs ready for retry
        /// </summary>
        Task<List<DownloadProcessingJob>> GetRetryJobsAsync();

        /// <summary>
        /// Update job status
        /// </summary>
        Task UpdateJobAsync(DownloadProcessingJob job);

        /// <summary>
        /// Get job by ID
        /// </summary>
        Task<DownloadProcessingJob?> GetJobAsync(string jobId);

        /// <summary>
        /// Get all jobs for a download
        /// </summary>
        Task<List<DownloadProcessingJob>> GetJobsForDownloadAsync(string downloadId);

        /// <summary>
        /// Get queue statistics
        /// </summary>
        Task<QueueStats> GetStatsAsync();

        /// <summary>
        /// Clean up old completed/failed jobs
        /// </summary>
        Task CleanupOldJobsAsync(int retentionDays = 7);

        /// <summary>
        /// Get recent processing activity for monitoring
        /// </summary>
        Task<List<DownloadProcessingJob>> GetRecentActivityAsync(int count = 50);
    }

}
