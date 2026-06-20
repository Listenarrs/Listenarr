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

namespace Listenarr.Application.Downloads.Contracts.Repositories
{
    public interface IDownloadProcessingJobRepository
    {
        Task<List<string>> GetPendingDownloadIdsAsync(IEnumerable<string> completedDownloadIds);
        Task<List<string>> GetAllJobDownloadIdsAsync(IEnumerable<string> completedDownloadIds);
        Task<DownloadProcessingJob?> GetActiveByDownloadIdAsync(string downloadId);
        Task<DownloadProcessingJob?> GetRecentCompletedByDownloadIdAsync(string downloadId, DateTime cutoff);
        Task<DownloadProcessingJob> AddAsync(DownloadProcessingJob job);

        /// <summary>
        /// List of all pending jobs with the given status
        /// </summary>
        Task<List<DownloadProcessingJob>> GetJobsByStatusAsync(ProcessingJobStatus status, CancellationToken cancellationToken = default);

        Task<List<DownloadProcessingJob>> GetDueRetryJobsAsync(CancellationToken cancellationToken = default);
        Task UpdateAsync(DownloadProcessingJob job);
        Task<DownloadProcessingJob?> GetByIdAsync(string jobId);
        Task<List<DownloadProcessingJob>> GetByDownloadIdAsync(string downloadId);
        Task<QueueStats> GetStatsAsync();
        Task CleanupOldJobsAsync(int retentionDays);
        Task<List<DownloadProcessingJob>> GetRecentAsync(int count);
        Task<List<DownloadProcessingJob>> GetStuckProcessingJobsAsync(CancellationToken cancellationToken = default);
    }
}
