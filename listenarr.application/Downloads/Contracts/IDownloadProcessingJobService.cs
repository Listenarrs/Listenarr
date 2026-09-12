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


namespace Listenarr.Application.Downloads.Contracts
{
    /// <summary>
    /// Service for managing download importation jobs
    /// </summary>
    public interface IDownloadProcessingJobService
    {
        /// <summary>
        /// Queue a download for importation
        /// </summary>
        /// <exception cref="InvalidOperationException">When download status is not Completed</exception>
        Task<string> EnqueueAsync(Download download);

        /// <summary>
        /// Put a download back on the import queue after its job gave up.
        ///
        /// Separate from <see cref="EnqueueAsync"/> because that one is driven by the download
        /// client reporting completion, and so requires the download to be Completed. A retry is
        /// asked for later, when the download has already moved on to ImportPending, and it reuses
        /// the existing job so the record of the earlier failures is not thrown away.
        /// </summary>
        /// <exception cref="InvalidOperationException">When the download is not awaiting importation</exception>
        Task<string> RequeueAsync(Download download);

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
        /// The download's job that is still pending, processing or awaiting retry, if any.
        /// </summary>
        /// <remarks>
        /// Null means nothing is going to import this download without being asked to.
        /// </remarks>
        Task<DownloadProcessingJob?> GetActiveJobAsync(string downloadId);

        /// <summary>
        /// Get queue statistics
        /// </summary>
        Task<QueueStats> GetStatsAsync();

        /// <summary>
        /// Clean up old terminal jobs after the retention window. Active jobs are never eligible,
        /// even if they are older than the retention cutoff.
        /// </summary>
        Task CleanupOldJobsAsync(int retentionDays = 7, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get recent processing activity for monitoring
        /// </summary>
        Task<List<DownloadProcessingJob>> GetRecentActivityAsync(int count = 50);

        /// <summary>
        /// Reset jobs that are stuck 
        /// </summary>
        Task ResetStuckJobsAsync(CancellationToken cancellationToken = default);
    }

}
