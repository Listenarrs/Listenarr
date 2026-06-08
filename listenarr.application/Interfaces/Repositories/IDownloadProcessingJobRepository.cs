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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces.Repositories
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

        /// <summary>
        /// Deletes jobs in the given <paramref name="statuses"/> whose <c>CompletedAt</c> is before
        /// <paramref name="cutoffUtc"/>, returning the number of rows removed. Pure data access: the
        /// retention policy (which statuses are terminal, how the cutoff is derived) is owned by the
        /// application layer and passed in.
        /// </summary>
        Task<int> DeleteCompletedBeforeAsync(IReadOnlyCollection<ProcessingJobStatus> statuses, DateTime cutoffUtc);
        Task<List<DownloadProcessingJob>> GetRecentAsync(int count);
        Task<List<DownloadProcessingJob>> GetStuckProcessingJobsAsync(CancellationToken cancellationToken = default);
    }
}
