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
    public interface IDownloadOrchestrator
    {
        Task<(bool Success, string Message, DownloadClientConfiguration? Client)> TestDownloadClientAsync(DownloadClientConfiguration client);
        Task<string> StartDownloadAsync(SearchResult searchResult, string downloadClientId, int? audiobookId = null);
        Task<List<Download>> GetActiveDownloadsAsync();
        Task<Download?> GetDownloadAsync(string downloadId);
        Task<bool> CancelDownloadAsync(string downloadId);
        Task UpdateDownloadStatusAsync();
        Task ProcessCompletedDownloadAsync(string downloadId, string finalPath);
        Task<string?> ReprocessDownloadAsync(string downloadId);
        Task<List<ReprocessResult>> ReprocessDownloadsAsync(List<string> downloadIds);
        Task<List<ReprocessResult>> ReprocessAllCompletedDownloadsAsync(bool includeProcessed = false, TimeSpan? maxAge = null);
        Task<SearchAndDownloadResult> SearchAndDownloadAsync(int audiobookId);
        Task<string> SendToDownloadClientAsync(SearchResult searchResult, string? downloadClientId = null, int? audiobookId = null);
        Task<List<QueueItem>> GetQueueAsync();
    }
}
