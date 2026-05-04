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
    public interface IDownloadRepository
    {
        Task<Download> AddAsync(Download download);
        Task<Download?> FindAsync(string id);
        Task UpdateAsync(Download download);
        Task UpdateMetadataAsync(string id, string key, object? value);
        Task RemoveAsync(string id);
        Task<List<Download>> GetAllAsync();
        Task<List<Download>> GetQueueDisplayCandidatesAsync();
        Task<List<Download>> GetQueueMatchingCandidatesAsync();
        Task<List<string>> GetKnownClientItemIdsAsync();
        Task<List<Download>> GetByClientAsync(string clientId);
        Task<Download> GetByIdAsync(string id);
        Task<List<Download>> GetByIdsAsync(IEnumerable<string> ids);
        Task<List<Download>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default);
        /// <summary>Returns downloads in Completed/ImportPending/Processing status ordered by CompletedAt, for processing job creation.</summary>
        Task<List<Download>> GetCompletionCandidatesAsync(int limit);

        /// <summary>
        /// Get all active downloads from database
        /// Include: 
        /// - Queued, Downloading, Paused, Processing (actively being monitored)
        /// - Completed without FinalPath (completed in client but not yet imported)
        /// - ImportPending without FinalPath (import attempted but still unresolved)
        /// - Moved with DownloadClientId (imported but deferred client removal pending;
        ///   we keep polling so CanBeRemoved metadata stays fresh for ProcessDeferredRemovalsAsync)
        /// Exclude:
        /// - ImportBlocked (blocked due to repeated failures, no point in retrying)
        /// - Failed, Cancelled (terminal states)
        /// </summary>
        Task<List<Download>> GetActiveAsync();

        /// <summary>Returns the most recent <paramref name="count"/> downloads ordered by StartedAt descending.</summary>
        Task<List<Download>> GetRecentAsync(int count);
        /// <summary>Returns distinct audiobook IDs for downloads whose status is in <paramref name="statuses"/>.</summary>
        Task<List<int>> GetActiveAudiobookIdsAsync(IEnumerable<DownloadStatus> statuses);
    }
}
