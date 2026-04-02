namespace Listenarr.Api.Services
{
    /// <seealso cref="Listenarr.Api.Services.Adapters.IDownloadClientAdapter"/>
    public interface IDownloadClientGateway
    {
        Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default);
        Task<Download?> AddAsync(DownloadClientConfiguration client, IndexerSearchResult result, CancellationToken ct = default);
        Task<bool> RemoveAsync(DownloadClientConfiguration client, Download download, bool deleteFiles = false, CancellationToken ct = default);
        Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default);
        Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default);
        Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, Download download, CancellationToken ct = default);
        Task<QueueItem> GetImportItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, QueueItem? previousAttempt = null, CancellationToken ct = default);
    }
}
