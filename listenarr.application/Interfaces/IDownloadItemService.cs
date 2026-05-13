using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Resolves download informations with accurate paths and metadata as queue items
    /// </summary>
    public interface IDownloadItemService
    {
        /// <summary>
        /// Resolves the import item by querying the download client.
        /// </summary>
        Task<QueueItem> GetImportItemAsync(Download download, CancellationToken cancellationToken = default);

        /// <summary>
        /// Filter the files from the queue item with the files on disk
        /// </summary>
        Task<List<string>> GetImportableFiles(Download download, QueueItem queueItem, CancellationToken cancellationToken = default);
    }
}
