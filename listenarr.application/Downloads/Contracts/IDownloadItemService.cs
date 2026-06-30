
namespace Listenarr.Application.Downloads.Contracts
{
    /// <summary>
    /// Resolves download information with accurate paths and metadata as queue items
    /// </summary>
    public interface IDownloadItemService
    {
        /// <summary>
        /// Resolves the import item by querying the download client.
        /// </summary>
        Task<QueueItem> GetImportItemAsync(Download download, CancellationToken cancellationToken = default);
    }
}
