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
    public interface IDownloadHistoryRepository
    {
        Task<DownloadHistory> AddAsync(DownloadHistory history, CancellationToken ct = default);
        Task<List<DownloadHistory>> GetByDownloadIdAsync(string downloadId, CancellationToken ct = default);
        Task<DownloadHistory?> GetLatestEventAsync(string downloadId, CancellationToken ct = default);
        Task<DownloadHistory?> GetImportedByDownloadIdAsync(string downloadId, CancellationToken ct = default);
        Task<List<DownloadHistory>> GetPendingImportsAsync(CancellationToken ct = default);
        Task MarkAsImportedAsync(string downloadId, CancellationToken ct = default);
        Task<bool> WasImportedAsync(string downloadId, CancellationToken ct = default);
        Task<List<DownloadHistory>> GetRecentAsync(int count = 100, CancellationToken ct = default);
        Task<List<DownloadHistory>> GetFailedDownloadsAsync(DateTime since, CancellationToken ct = default);
        Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default);
        Task<int> GetCountAsync(CancellationToken ct = default);
    }
}
