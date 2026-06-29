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

namespace Listenarr.Application.ActivityHistory.Contracts.Repositories
{
    public interface IHistoryRepository
    {
        Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken ct = default);
        Task<History?> GetByIdAsync(int id, CancellationToken ct = default);
        Task<List<History>> GetByCorrelationIdAsync(string correlationId, CancellationToken ct = default);
        Task<History?> GetSucceededImportedByDownloadIdAsync(string downloadId, CancellationToken ct = default);
        Task<DateTime?> GetOldestTimestampByDownloadIdAsync(string downloadId, CancellationToken ct = default);
        Task<List<History>> GetPagedAsync(int limit, int offset, CancellationToken ct = default);
        Task<int> CountAsync(CancellationToken ct = default);
        Task<List<History>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default);
        Task<List<History>> GetByEventTypeAsync(string eventType, int? limit = null, CancellationToken ct = default);
        Task<List<History>> GetBySourceAsync(string source, int? limit = null, CancellationToken ct = default);
        Task<List<History>> GetRecentAsync(int limit, CancellationToken ct = default);
        Task<History> AddAsync(History entry, CancellationToken ct = default);
        Task<bool> DeleteAsync(int id, CancellationToken ct = default);
        Task DeleteAllAsync(CancellationToken ct = default);
        Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
    }
}
