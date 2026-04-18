using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    public interface IDownloadOrchestrator
    {
        Task<(bool Success, string Message, DownloadClientConfiguration? Client)> TestDownloadClientAsync(DownloadClientConfiguration client);
        Task<string?> StartDownloadAsync(SearchResult searchResult, string downloadClientId, int? audiobookId = null);
        Task<List<Download>> GetActiveDownloadsAsync();
        Task<Download?> GetDownloadAsync(string downloadId);
        Task<bool> CancelDownloadAsync(string downloadId);
        Task UpdateDownloadStatusAsync();
        Task ProcessCompletedDownloadAsync(string downloadId, string finalPath);
        Task<string?> ReprocessDownloadAsync(string downloadId);
        Task<List<ReprocessResult>> ReprocessDownloadsAsync(List<string> downloadIds);
        Task<List<ReprocessResult>> ReprocessAllCompletedDownloadsAsync(bool includeProcessed = false, TimeSpan? maxAge = null);
        Task<SearchAndDownloadResult> SearchAndDownloadAsync(int audiobookId);
        Task<string?> SendToDownloadClientAsync(SearchResult searchResult, string? downloadClientId = null, int? audiobookId = null);
        Task<List<QueueItem>> GetQueueAsync();
    }
}
